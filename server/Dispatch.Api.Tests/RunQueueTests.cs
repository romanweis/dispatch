using Microsoft.Extensions.DependencyInjection;
using Dispatch.Api.Data;
using Dispatch.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dispatch.Api.Tests;

/// <summary>End-to-end run execution against the simulated claude launcher (FakeIncus mode).</summary>
public sealed class RunQueueTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public Task InitializeAsync()
    {
        _host = new TestHost();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Refine_run_records_events_session_and_applies_outcome()
    {
        var ticket = await _host.CreateTicketAsync();
        long runId;
        using (var scope = _host.Scope())
        {
            runId = (await _host.Tickets(scope).RefineAsync(ticket.Id)).Id;
        }

        var subscription = _host.Services.GetRequiredService<EventBus>().Subscribe();
        await MarkRunningAsync(runId);
        await _host.Queue.ExecuteRunAsync(runId, CancellationToken.None);

        using var verify = _host.Scope();
        var db = _host.Db(verify);
        var run = await db.Runs.AsNoTracking().FirstAsync(r => r.Id == runId);
        var events = await db.RunEvents.AsNoTracking().Where(e => e.RunId == runId).OrderBy(e => e.Seq).ToListAsync();
        var reloaded = await _host.ReloadAsync(ticket.Id);

        Assert.True(run.Status == RunStatus.Done, $"run status {run.Status}: {run.Error}");
        Assert.Equal(0, run.ExitCode);
        Assert.NotNull(run.SessionId);
        Assert.StartsWith("fake-", run.SessionId);
        Assert.Equal(run.SessionId, reloaded.ClaudeSessionId);
        Assert.Equal($"t-{ticket.Id}", reloaded.Container);

        Assert.Equal(new long[] { 1, 2, 3 }, events.Select(e => e.Seq).ToArray());
        Assert.Equal("system", events[0].Payload.RootElement.GetProperty("type").GetString());
        Assert.Equal("assistant", events[1].Payload.RootElement.GetProperty("type").GetString());
        Assert.Equal("result", events[2].Payload.RootElement.GetProperty("type").GetString());

        // Refinement without questions or spec is not actionable -> failed.
        Assert.Equal(TicketStatus.Failed, reloaded.Status);

        var published = new List<BusMessage>();
        while (subscription.Reader.TryRead(out var m))
        {
            published.Add(m);
        }

        Assert.Equal(3, published.Count(m => m.Kind == BusMessageKind.RunEvent));
        Assert.Contains(published, m => m.Kind == BusMessageKind.Run && ((RunDto)m.Payload).Status == RunStatus.Done);
        Assert.Contains(published, m => m.Kind == BusMessageKind.Ticket);
        subscription.Dispose();
    }

    [Fact]
    public async Task Refine_run_with_spec_submitted_leads_to_ready()
    {
        var ticket = await _host.CreateTicketAsync();
        long runId;
        using (var scope = _host.Scope())
        {
            var tickets = _host.Tickets(scope);
            runId = (await tickets.RefineAsync(ticket.Id)).Id;
            await tickets.SubmitSpecAsync(ticket.Id, "spec written by the agent");
        }

        await MarkRunningAsync(runId);
        await _host.Queue.ExecuteRunAsync(runId, CancellationToken.None);

        var reloaded = await _host.ReloadAsync(ticket.Id);
        Assert.Equal(TicketStatus.Ready, reloaded.Status);
        Assert.Equal("spec written by the agent", reloaded.Spec);
    }

    [Fact]
    public async Task Refine_run_with_questions_leads_to_needs_input()
    {
        var ticket = await _host.CreateTicketAsync();
        long runId;
        using (var scope = _host.Scope())
        {
            var tickets = _host.Tickets(scope);
            runId = (await tickets.RefineAsync(ticket.Id)).Id;
            await tickets.AddQuestionsAsync(ticket.Id, ["Which DB?"]);
        }

        await MarkRunningAsync(runId);
        await _host.Queue.ExecuteRunAsync(runId, CancellationToken.None);

        Assert.Equal(TicketStatus.NeedsInput, (await _host.ReloadAsync(ticket.Id)).Status);
    }

    [Fact]
    public async Task Work_run_pulls_state_and_moves_to_review_on_gate_passed()
    {
        var ticket = await _host.CreateTicketAsync();
        await _host.SetAsync(ticket.Id, t =>
        {
            t.Status = TicketStatus.Ready;
            t.Spec = "spec";
        });
        long runId;
        using (var scope = _host.Scope())
        {
            runId = (await _host.Tickets(scope).StartAsync(ticket.Id)).Id;
        }

        _host.Incus.SetFile($"t-{ticket.Id}", $"/home/agent/demo/orchestrator/tasks/{ticket.Id}/state.json", "{\"gate\":\"PASSED\"}");

        await MarkRunningAsync(runId);
        await _host.Queue.ExecuteRunAsync(runId, CancellationToken.None);

        var reloaded = await _host.ReloadAsync(ticket.Id);
        Assert.Equal(TicketStatus.Review, reloaded.Status);
        Assert.Equal("PASSED", reloaded.WorkflowState!.RootElement.GetProperty("gate").GetString());
    }

    [Fact]
    public async Task Work_run_with_auto_merge_queues_ship_run_instead_of_review()
    {
        var ticket = await _host.CreateTicketAsync();
        await _host.SetAsync(ticket.Id, t =>
        {
            t.Status = TicketStatus.Ready;
            t.Spec = "spec";
            t.AutoMerge = true;
        });
        long runId;
        using (var scope = _host.Scope())
        {
            runId = (await _host.Tickets(scope).StartAsync(ticket.Id)).Id;
        }

        _host.Incus.SetFile($"t-{ticket.Id}", $"/home/agent/demo/orchestrator/tasks/{ticket.Id}/state.json", "{\"gate\":\"PASSED\"}");

        await MarkRunningAsync(runId);
        await _host.Queue.ExecuteRunAsync(runId, CancellationToken.None);

        var reloaded = await _host.ReloadAsync(ticket.Id);
        Assert.Equal(TicketStatus.InProgress, reloaded.Status);

        using var verify = _host.Scope();
        var runs = await _host.Db(verify).Runs.AsNoTracking().Where(r => r.TicketId == ticket.Id).OrderBy(r => r.Id).ToListAsync();
        Assert.Equal(2, runs.Count);
        Assert.Equal(RunStatus.Done, runs[0].Status);
        Assert.Equal(RunKind.Ship, runs[1].Kind);
        Assert.Equal(RunStatus.Pending, runs[1].Status);
        Assert.Contains($"/ship-feature {ticket.Id}", runs[1].Prompt);
    }

    [Fact]
    public async Task Ship_run_with_shipped_progress_closes_ticket_and_deletes_container()
    {
        var ticket = await _host.CreateTicketAsync();
        await _host.SetAsync(ticket.Id, t =>
        {
            t.Status = TicketStatus.Review;
            t.Slug = "s";
            t.ClaudeSessionId = "sess";
            t.WorkflowState = System.Text.Json.JsonDocument.Parse("{\"gate\":\"PASSED\"}");
        });
        long runId;
        using (var scope = _host.Scope())
        {
            var tickets = _host.Tickets(scope);
            runId = (await tickets.ShipAsync(ticket.Id)).Id;
            await tickets.AddProgressAsync(ticket.Id, "shipping", null);
            await tickets.AddProgressAsync(ticket.Id, "shipped", "backend academy");
        }

        _host.Incus.SetFile($"t-{ticket.Id}", $"/home/agent/demo/orchestrator/tasks/{ticket.Id}/state.json", "{\"gate\":\"PASSED\",\"phase\":\"shipped\"}");

        await MarkRunningAsync(runId);
        await _host.Queue.ExecuteRunAsync(runId, CancellationToken.None);

        var reloaded = await _host.ReloadAsync(ticket.Id);
        Assert.Equal(TicketStatus.Done, reloaded.Status);
        Assert.Null(reloaded.Container);
        Assert.Contains($"delete t-{ticket.Id}", _host.Incus.Calls);
    }

    [Fact]
    public async Task Ship_run_that_halts_returns_to_review()
    {
        var ticket = await _host.CreateTicketAsync();
        await _host.SetAsync(ticket.Id, t =>
        {
            t.Status = TicketStatus.Review;
            t.Slug = "s";
            t.ClaudeSessionId = "sess";
            t.AutoMerge = true;
            t.WorkflowState = System.Text.Json.JsonDocument.Parse("{\"gate\":\"PASSED\"}");
        });
        long runId;
        using (var scope = _host.Scope())
        {
            var tickets = _host.Tickets(scope);
            runId = (await tickets.ShipAsync(ticket.Id)).Id;
            await tickets.AddProgressAsync(ticket.Id, "halted", "PROBE_INCONCLUSIVE");
        }

        _host.Incus.SetFile($"t-{ticket.Id}", $"/home/agent/demo/orchestrator/tasks/{ticket.Id}/state.json", "{\"gate\":\"PASSED\"}");

        await MarkRunningAsync(runId);
        await _host.Queue.ExecuteRunAsync(runId, CancellationToken.None);

        var reloaded = await _host.ReloadAsync(ticket.Id);
        Assert.Equal(TicketStatus.Review, reloaded.Status);
        Assert.NotNull(reloaded.Container);

        // No second ship run is queued: auto-merge never re-triggers itself.
        using var verify = _host.Scope();
        Assert.Equal(1, await _host.Db(verify).Runs.CountAsync(r => r.TicketId == ticket.Id));
    }

    [Fact]
    public void Session_options_follow_project_claude_config()
    {
        var ticket = new Ticket { Id = 5, Title = "t", Token = "x", ClaudeSessionId = "sess-5", ProjectId = _host.ProjectId };
        _host.Project.Config.Claude.Model = "claude-opus-4-1";

        var work = _host.Queue.BuildSessionOptions(new Run { Kind = RunKind.Work, Prompt = "go", TicketId = 5 }, ticket, _host.Project);
        var refine = _host.Queue.BuildSessionOptions(new Run { Kind = RunKind.Refine, Prompt = "refine", TicketId = 5 }, ticket, _host.Project);

        Assert.Equal("go", work.InitialPrompt);
        Assert.Equal("sess-5", work.ResumeSessionId);
        Assert.Equal("bypassPermissions", work.PermissionMode);
        Assert.Equal(["--max-turns", "400", "--model", "claude-opus-4-1"], work.AdditionalArgs!);
        Assert.Equal(["--max-turns", "60", "--model", "claude-opus-4-1"], refine.AdditionalArgs!);
        Assert.Null(work.ExecutablePrefix);

        ticket.ClaudeSessionId = null;
        var fresh = _host.Queue.BuildSessionOptions(new Run { Kind = RunKind.Refine, Prompt = "refine", TicketId = 5 }, ticket, _host.Project);
        Assert.Null(fresh.ResumeSessionId);
    }

    private async Task MarkRunningAsync(long runId)
    {
        using var scope = _host.Scope();
        var db = _host.Db(scope);
        var run = await db.Runs.FirstAsync(r => r.Id == runId);
        run.Status = RunStatus.Running;
        run.StartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
}
