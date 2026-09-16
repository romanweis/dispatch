using Microsoft.Extensions.DependencyInjection;
using Dispatch.Api;
using Dispatch.Api.Data;
using Dispatch.Api.Incus;
using Dispatch.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dispatch.Api.Tests;

public sealed class TicketServiceTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public Task InitializeAsync()
    {
        _host = new TestHost();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Create_starts_in_backlog_with_token()
    {
        var ticket = await _host.CreateTicketAsync();

        Assert.Equal(TicketStatus.Backlog, ticket.Status);
        Assert.Equal(64, ticket.Token.Length);
        Assert.Null(ticket.Slug);
    }

    [Fact]
    public async Task Refine_from_backlog_queues_refine_run_and_sets_refining()
    {
        var ticket = await _host.CreateTicketAsync();

        using var scope = _host.Scope();
        var run = await _host.Tickets(scope).RefineAsync(ticket.Id);

        Assert.Equal(RunKind.Refine, run.Kind);
        Assert.Equal(RunStatus.Pending, run.Status);
        Assert.Equal($"REFINE {ticket.Id} Add login page", run.Prompt);
        Assert.Equal(TicketStatus.Refining, (await _host.ReloadAsync(ticket.Id)).Status);
    }

    [Theory]
    [InlineData(TicketStatus.Ready)]
    [InlineData(TicketStatus.InProgress)]
    [InlineData(TicketStatus.Review)]
    [InlineData(TicketStatus.Done)]
    public async Task Refine_from_other_states_is_invalid_transition(TicketStatus status)
    {
        var ticket = await _host.CreateTicketAsync();
        await _host.SetAsync(ticket.Id, t => t.Status = status);

        using var scope = _host.Scope();
        var ex = await Assert.ThrowsAsync<DispatchException>(() => _host.Tickets(scope).RefineAsync(ticket.Id));

        Assert.Equal("invalid_transition", ex.Code);
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task Refine_with_active_run_is_409()
    {
        var ticket = await _host.CreateTicketAsync();
        using (var scope = _host.Scope())
        {
            await _host.Tickets(scope).RefineAsync(ticket.Id);
        }

        await _host.SetAsync(ticket.Id, t => t.Status = TicketStatus.Backlog);

        using var scope2 = _host.Scope();
        var ex = await Assert.ThrowsAsync<DispatchException>(() => _host.Tickets(scope2).RefineAsync(ticket.Id));
        Assert.Equal("run_active", ex.Code);
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public async Task Refine_from_needs_input_requires_all_questions_answered()
    {
        var ticket = await _host.CreateTicketAsync();
        using (var scope = _host.Scope())
        {
            await _host.Tickets(scope).AddQuestionsAsync(ticket.Id, ["Which DB?"]);
        }

        Assert.Equal(TicketStatus.NeedsInput, (await _host.ReloadAsync(ticket.Id)).Status);

        using var scope2 = _host.Scope();
        var ex = await Assert.ThrowsAsync<DispatchException>(() => _host.Tickets(scope2).RefineAsync(ticket.Id));
        Assert.Equal("invalid_transition", ex.Code);
    }

    [Fact]
    public async Task Answer_stores_answers_and_queues_answer_run()
    {
        var ticket = await _host.CreateTicketAsync();
        List<Question> questions;
        using (var scope = _host.Scope())
        {
            questions = await _host.Tickets(scope).AddQuestionsAsync(ticket.Id, ["Which DB?", "Which UI?"]);
        }

        await _host.SetAsync(ticket.Id, t => t.ClaudeSessionId = "sess-1");

        using var scope2 = _host.Scope();
        var run = await _host.Tickets(scope2).AnswerAsync(ticket.Id,
        [
            new AnswerItem(questions[0].Id, "Postgres"),
            new AnswerItem(questions[1].Id, "React"),
        ]);

        Assert.Equal(RunKind.Answer, run.Kind);
        Assert.Contains("Q: Which DB?\nA: Postgres", run.Prompt);
        Assert.Contains("Q: Which UI?\nA: React", run.Prompt);
        Assert.Equal(TicketStatus.Refining, (await _host.ReloadAsync(ticket.Id)).Status);

        var db = _host.Db(scope2);
        Assert.Equal(0, await db.Questions.CountAsync(q => q.TicketId == ticket.Id && q.Answer == null));
    }

    [Fact]
    public async Task Answer_from_wrong_state_is_invalid_transition()
    {
        var ticket = await _host.CreateTicketAsync();
        using var scope = _host.Scope();
        var ex = await Assert.ThrowsAsync<DispatchException>(() =>
            _host.Tickets(scope).AnswerAsync(ticket.Id, [new AnswerItem(1, "x")]));
        Assert.Equal("invalid_transition", ex.Code);
    }

    [Fact]
    public async Task Start_requires_ready_and_spec()
    {
        var ticket = await _host.CreateTicketAsync();

        using (var scope = _host.Scope())
        {
            var ex = await Assert.ThrowsAsync<DispatchException>(() => _host.Tickets(scope).StartAsync(ticket.Id));
            Assert.Equal("invalid_transition", ex.Code);
        }

        await _host.SetAsync(ticket.Id, t => t.Status = TicketStatus.Ready);
        using (var scope = _host.Scope())
        {
            var ex = await Assert.ThrowsAsync<DispatchException>(() => _host.Tickets(scope).StartAsync(ticket.Id));
            Assert.Equal("invalid_transition", ex.Code);
            Assert.Contains("spec", ex.Message);
        }
    }

    [Fact]
    public async Task Start_sets_slug_writes_feature_file_commits_and_queues_work_run()
    {
        var ticket = await _host.CreateTicketAsync("Add Login Page For Admin Users");
        await _host.SetAsync(ticket.Id, t =>
        {
            t.Status = TicketStatus.Ready;
            t.Spec = "# Spec\nDo the thing.";
        });

        using var scope = _host.Scope();
        var run = await _host.Tickets(scope).StartAsync(ticket.Id);
        var reloaded = await _host.ReloadAsync(ticket.Id);

        Assert.Equal(RunKind.Work, run.Kind);
        Assert.Equal(TicketStatus.InProgress, reloaded.Status);
        Assert.Equal($"t-{ticket.Id}", reloaded.Container);
        Assert.NotNull(reloaded.Slug);
        Assert.Matches(@"^add-login-page-for-[a-z0-9]{3}$", reloaded.Slug);
        Assert.Equal($"WORK {ticket.Id} {reloaded.Slug} /home/agent/demo", run.Prompt);

        var calls = _host.Incus.Calls.ToList();
        Assert.Contains($"ensure t-{ticket.Id}", calls);
        Assert.Contains($"push t-{ticket.Id}/home/agent/demo/orchestrator/features/{ticket.Id}-{reloaded.Slug}.md", calls);
        Assert.Contains(calls, c => c.StartsWith($"exec t-{ticket.Id} git") && c.Contains($"commit -m feat({ticket.Id}): {reloaded.Slug}"));
        Assert.Contains(calls, c => c.StartsWith($"exec t-{ticket.Id} git") && c.EndsWith(" push"));
        Assert.Equal(ContainerState.Running, await _host.Incus.GetStateAsync($"t-{ticket.Id}"));

        // Remote main may have moved since the container was created: rebase before pushing.
        var pullIndex = calls.FindIndex(c => c.StartsWith($"exec t-{ticket.Id} git") && c.EndsWith(" pull --rebase"));
        var pushIndex = calls.FindIndex(c => c.StartsWith($"exec t-{ticket.Id} git") && c.EndsWith(" push"));
        Assert.True(pullIndex >= 0, "expected git pull --rebase before push");
        Assert.True(pullIndex < pushIndex, "git pull --rebase must run before git push");
    }

    [Fact]
    public async Task Start_fails_and_aborts_rebase_when_pull_rebase_fails()
    {
        var ticket = await _host.CreateTicketAsync();
        await _host.SetAsync(ticket.Id, t =>
        {
            t.Status = TicketStatus.Ready;
            t.Spec = "spec";
        });
        _host.Incus.ExecHandler = (_, args) =>
            args.Contains("pull") ? new ExecResult(1, "", "CONFLICT (content)") : new ExecResult(0, "", "");

        using var scope = _host.Scope();
        var ex = await Assert.ThrowsAsync<DispatchException>(() => _host.Tickets(scope).StartAsync(ticket.Id));
        Assert.Equal("git_failed", ex.Code);
        Assert.Contains("pull --rebase", ex.Message);

        var calls = _host.Incus.Calls.ToList();
        Assert.Contains(calls, c => c.StartsWith($"exec t-{ticket.Id} git") && c.EndsWith(" rebase --abort"));
        Assert.DoesNotContain(calls, c => c.StartsWith($"exec t-{ticket.Id} git") && c.EndsWith(" push"));
        Assert.Equal(TicketStatus.Ready, (await _host.ReloadAsync(ticket.Id)).Status);
    }

    [Fact]
    public async Task Start_tolerates_nothing_to_commit()
    {
        var ticket = await _host.CreateTicketAsync();
        await _host.SetAsync(ticket.Id, t =>
        {
            t.Status = TicketStatus.Ready;
            t.Spec = "spec";
        });
        _host.Incus.ExecHandler = (_, args) =>
            args.Contains("commit") ? new ExecResult(1, "nothing to commit, working tree clean", "") : new ExecResult(0, "", "");

        using var scope = _host.Scope();
        var run = await _host.Tickets(scope).StartAsync(ticket.Id);
        Assert.Equal(RunStatus.Pending, run.Status);
    }

    [Fact]
    public async Task Start_fails_when_git_push_fails()
    {
        var ticket = await _host.CreateTicketAsync();
        await _host.SetAsync(ticket.Id, t =>
        {
            t.Status = TicketStatus.Ready;
            t.Spec = "spec";
        });
        _host.Incus.ExecHandler = (_, args) =>
            args.Contains("push") ? new ExecResult(1, "", "rejected") : new ExecResult(0, "", "");

        using var scope = _host.Scope();
        var ex = await Assert.ThrowsAsync<DispatchException>(() => _host.Tickets(scope).StartAsync(ticket.Id));
        Assert.Equal("git_failed", ex.Code);
        Assert.Equal(TicketStatus.Ready, (await _host.ReloadAsync(ticket.Id)).Status);
    }

    [Fact]
    public async Task Resume_requires_session_and_no_active_run()
    {
        var ticket = await _host.CreateTicketAsync();
        using (var scope = _host.Scope())
        {
            var ex = await Assert.ThrowsAsync<DispatchException>(() => _host.Tickets(scope).ResumeAsync(ticket.Id, "hi"));
            Assert.Equal("invalid_transition", ex.Code);
        }

        await _host.SetAsync(ticket.Id, t =>
        {
            t.ClaudeSessionId = "sess-9";
            t.Status = TicketStatus.Review;
        });

        using (var scope = _host.Scope())
        {
            var run = await _host.Tickets(scope).ResumeAsync(ticket.Id, "please also add tests");
            Assert.Equal(RunKind.Resume, run.Kind);
            Assert.Equal("please also add tests", run.Prompt);
        }

        using (var scope = _host.Scope())
        {
            var ex = await Assert.ThrowsAsync<DispatchException>(() => _host.Tickets(scope).ResumeAsync(ticket.Id, "again"));
            Assert.Equal("run_active", ex.Code);
        }
    }

    [Fact]
    public async Task Cancel_marks_run_cancelled_and_ticket_failed()
    {
        var ticket = await _host.CreateTicketAsync();
        long runId;
        using (var scope = _host.Scope())
        {
            runId = (await _host.Tickets(scope).RefineAsync(ticket.Id)).Id;
        }

        using (var scope = _host.Scope())
        {
            var run = await _host.Tickets(scope).CancelAsync(ticket.Id);
            Assert.Equal(runId, run.Id);
            Assert.Equal(RunStatus.Cancelled, run.Status);
        }

        Assert.Equal(TicketStatus.Failed, (await _host.ReloadAsync(ticket.Id)).Status);

        using (var scope = _host.Scope())
        {
            var ex = await Assert.ThrowsAsync<DispatchException>(() => _host.Tickets(scope).CancelAsync(ticket.Id));
            Assert.Equal("invalid_transition", ex.Code);
        }
    }

    [Fact]
    public async Task Done_deletes_container_with_optional_snapshot()
    {
        var ticket = await _host.CreateTicketAsync();
        await _host.SetAsync(ticket.Id, t =>
        {
            t.Status = TicketStatus.Review;
            t.Container = $"t-{ticket.Id}";
        });
        await _host.Incus.EnsureTicketContainerAsync(await _host.ReloadAsync(ticket.Id), _host.Project);

        using var scope = _host.Scope();
        var done = await _host.Tickets(scope).DoneAsync(ticket.Id, snapshot: true);

        Assert.Equal(TicketStatus.Done, done.Status);
        Assert.Null(done.Container);
        Assert.Contains($"snapshot+delete t-{ticket.Id}", _host.Incus.Calls);
        Assert.Equal(ContainerState.Missing, await _host.Incus.GetStateAsync($"t-{ticket.Id}"));
    }

    [Fact]
    public async Task Ship_requires_review_with_passed_gate_and_queues_ship_run()
    {
        var ticket = await _host.CreateTicketAsync();
        await _host.SetAsync(ticket.Id, t =>
        {
            t.Status = TicketStatus.Review;
            t.Slug = "add-login-page-abc";
            t.ClaudeSessionId = "sess";
            t.WorkflowState = System.Text.Json.JsonDocument.Parse("{\"gate\":\"RUNNING\"}");
        });

        using (var scope = _host.Scope())
        {
            var ex = await Assert.ThrowsAsync<DispatchException>(() => _host.Tickets(scope).ShipAsync(ticket.Id));
            Assert.Equal("invalid_transition", ex.Code);
        }

        await _host.SetAsync(ticket.Id, t => t.WorkflowState = System.Text.Json.JsonDocument.Parse("{\"gate\":\"PASSED\"}"));

        using var scope2 = _host.Scope();
        var run = await _host.Tickets(scope2).ShipAsync(ticket.Id);

        Assert.Equal(RunKind.Ship, run.Kind);
        Assert.Contains($"/ship-feature {ticket.Id}", run.Prompt);
        Assert.Equal(TicketStatus.InProgress, (await _host.ReloadAsync(ticket.Id)).Status);

        var ex2 = await Assert.ThrowsAsync<DispatchException>(() => _host.Tickets(scope2).ShipAsync(ticket.Id));
        Assert.Equal("run_active", ex2.Code);
    }

    [Fact]
    public async Task Patch_toggles_auto_merge_and_create_accepts_it()
    {
        var ticket = await _host.CreateTicketAsync();
        Assert.False(ticket.AutoMerge);

        using var scope = _host.Scope();
        var tickets = _host.Tickets(scope);
        var patched = await tickets.PatchAsync(ticket.Id, null, null, null, autoMerge: true);
        Assert.True(patched.AutoMerge);
        Assert.True((await _host.ReloadAsync(ticket.Id)).AutoMerge);

        var created = await tickets.CreateAsync(_host.ProjectId, "auto", null, autoMerge: true);
        Assert.True(created.AutoMerge);
    }

    [Fact]
    public async Task Done_from_backlog_is_invalid()
    {
        var ticket = await _host.CreateTicketAsync();
        using var scope = _host.Scope();
        var ex = await Assert.ThrowsAsync<DispatchException>(() => _host.Tickets(scope).DoneAsync(ticket.Id, false));
        Assert.Equal("invalid_transition", ex.Code);
    }

    [Fact]
    public async Task Move_only_between_non_running_states()
    {
        var ticket = await _host.CreateTicketAsync();

        using (var scope = _host.Scope())
        {
            var moved = await _host.Tickets(scope).MoveAsync(ticket.Id, "ready");
            Assert.Equal(TicketStatus.Ready, moved.Status);
        }

        using (var scope = _host.Scope())
        {
            var ex = await Assert.ThrowsAsync<DispatchException>(() => _host.Tickets(scope).MoveAsync(ticket.Id, "in_progress"));
            Assert.Equal("invalid_transition", ex.Code);
        }

        using (var scope = _host.Scope())
        {
            var ex = await Assert.ThrowsAsync<DispatchException>(() => _host.Tickets(scope).MoveAsync(ticket.Id, "bogus"));
            Assert.Equal("bad_request", ex.Code);
        }
    }

    [Fact]
    public async Task Patch_spec_only_in_editable_states()
    {
        var ticket = await _host.CreateTicketAsync();
        using (var scope = _host.Scope())
        {
            var patched = await _host.Tickets(scope).PatchAsync(ticket.Id, "New title", null, "new spec");
            Assert.Equal("New title", patched.Title);
            Assert.Equal("new spec", patched.Spec);
        }

        await _host.SetAsync(ticket.Id, t => t.Status = TicketStatus.InProgress);
        using (var scope = _host.Scope())
        {
            var ex = await Assert.ThrowsAsync<DispatchException>(() => _host.Tickets(scope).PatchAsync(ticket.Id, null, null, "x"));
            Assert.Equal("invalid_transition", ex.Code);

            // title/body still editable
            var patched = await _host.Tickets(scope).PatchAsync(ticket.Id, null, "new body", null);
            Assert.Equal("new body", patched.Body);
        }
    }

    [Fact]
    public async Task Questions_during_run_are_tagged_with_run_id_and_do_not_change_status_yet()
    {
        var ticket = await _host.CreateTicketAsync();
        long runId;
        using (var scope = _host.Scope())
        {
            runId = (await _host.Tickets(scope).RefineAsync(ticket.Id)).Id;
        }

        using (var scope = _host.Scope())
        {
            var created = await _host.Tickets(scope).AddQuestionsAsync(ticket.Id, ["Q1"]);
            Assert.Equal(runId, created[0].RunId);
        }

        Assert.Equal(TicketStatus.Refining, (await _host.ReloadAsync(ticket.Id)).Status);
    }

    [Fact]
    public async Task Spec_during_run_flags_run_and_without_run_moves_to_ready()
    {
        var ticket = await _host.CreateTicketAsync();
        long runId;
        using (var scope = _host.Scope())
        {
            runId = (await _host.Tickets(scope).RefineAsync(ticket.Id)).Id;
            await _host.Tickets(scope).SubmitSpecAsync(ticket.Id, "the spec");
        }

        using (var scope = _host.Scope())
        {
            var run = await _host.Db(scope).Runs.AsNoTracking().FirstAsync(r => r.Id == runId);
            Assert.True(run.SpecSubmitted);
            Assert.Equal(TicketStatus.Refining, (await _host.ReloadAsync(ticket.Id)).Status);
        }

        var second = await _host.CreateTicketAsync("Other");
        using (var scope = _host.Scope())
        {
            await _host.Tickets(scope).SubmitSpecAsync(second.Id, "spec");
        }

        Assert.Equal(TicketStatus.Ready, (await _host.ReloadAsync(second.Id)).Status);
    }

    [Fact]
    public async Task Refresh_from_container_moves_in_progress_to_review_on_gate_passed()
    {
        var ticket = await _host.CreateTicketAsync();
        await _host.SetAsync(ticket.Id, t =>
        {
            t.Status = TicketStatus.InProgress;
            t.Container = $"t-{ticket.Id}";
        });
        _host.Incus.SetFile($"t-{ticket.Id}", $"/home/agent/demo/orchestrator/tasks/{ticket.Id}/state.json", "{\"gate\":\"PASSED\",\"phase\":\"done\"}");
        _host.Incus.SetFile($"t-{ticket.Id}", $"/home/agent/demo/orchestrator/tasks/{ticket.Id}/result.json", "{\"pr\":42}");

        using var scope = _host.Scope();
        var refreshed = await _host.Tickets(scope).RefreshFromContainerAsync(ticket.Id);

        Assert.Equal(TicketStatus.Review, refreshed.Status);
        Assert.Equal("PASSED", refreshed.WorkflowState!.RootElement.GetProperty("gate").GetString());
        Assert.Equal(42, refreshed.Result!.RootElement.GetProperty("pr").GetInt32());

        // Round-trips through the JsonDocument <-> string converter.
        var stored = await _host.ReloadAsync(ticket.Id);
        Assert.Equal("done", stored.WorkflowState!.RootElement.GetProperty("phase").GetString());
    }

    [Fact]
    public async Task Delete_removes_ticket_and_container()
    {
        var ticket = await _host.CreateTicketAsync();
        await _host.SetAsync(ticket.Id, t => t.Container = $"t-{ticket.Id}");

        using var scope = _host.Scope();
        await _host.Tickets(scope).DeleteAsync(ticket.Id);

        Assert.Contains($"delete t-{ticket.Id}", _host.Incus.Calls);
        Assert.False(await _host.Db(scope).Tickets.AnyAsync(t => t.Id == ticket.Id));
    }

    [Fact]
    public async Task Ticket_dto_carries_counts_active_run_and_last_progress()
    {
        var ticket = await _host.CreateTicketAsync();
        long runId;
        using (var scope = _host.Scope())
        {
            var tickets = _host.Tickets(scope);
            runId = (await tickets.RefineAsync(ticket.Id)).Id;
            await tickets.AddQuestionsAsync(ticket.Id, ["A", "B"]);
            await tickets.AddProgressAsync(ticket.Id, "investigating", null);
            await tickets.AddProgressAsync(ticket.Id, "writing-spec", "almost there");
        }

        using var scope2 = _host.Scope();
        var dto = await scope2.ServiceProvider.GetRequiredService<DtoMapper>().TicketDetailAsync(ticket.Id);

        Assert.Equal(2, dto.OpenQuestions);
        Assert.Equal(runId, dto.ActiveRunId);
        Assert.Equal("none", dto.ContainerState);
        Assert.Equal("demo", dto.ProjectName);
        Assert.NotNull(dto.LastProgress);
        Assert.Equal("writing-spec", dto.LastProgress!.Phase);
        Assert.Equal("almost there", dto.LastProgress.Note);
        Assert.Single(dto.Runs);
        Assert.Equal(2, dto.Questions.Count);
        Assert.Contains($"incus exec t-{ticket.Id} --user 1000 --group 1000 --cwd /home/agent/demo/orchestrator", dto.AttachCommand);
    }
}
