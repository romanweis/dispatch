using System.Text.Json;
using Dispatch.Api.Config;
using Dispatch.Api.Data;
using Dispatch.Api.Incus;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Api.Services;

/// <summary>The ticket state machine. Every transition rule from docs/api.md lives here.</summary>
public sealed class TicketService(
    DispatchDbContext db,
    IIncusService incus,
    IProjectRegistry registry,
    EventBus bus,
    RunQueueSignal queueSignal,
    RunCancellationRegistry cancellations,
    DtoMapper mapper,
    ILogger<TicketService> logger)
{
    private static readonly TicketStatus[] SpecEditable = [TicketStatus.Ready, TicketStatus.NeedsInput, TicketStatus.Backlog];
    private static readonly TicketStatus[] RunningStates = [TicketStatus.Refining, TicketStatus.InProgress];

    // ---- CRUD ---------------------------------------------------------------

    public async Task<Ticket> CreateAsync(
        int projectId, string title, string? body, bool autoMerge = false, TicketType type = TicketType.Feature, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw DispatchException.BadRequest("title is required");
        }

        var projectExists = await db.Projects.AnyAsync(p => p.Id == projectId, ct);
        if (!projectExists)
        {
            throw DispatchException.NotFound("Project", projectId);
        }

        var now = DateTime.UtcNow;
        var ticket = new Ticket
        {
            ProjectId = projectId,
            Title = title.Trim(),
            Body = body ?? "",
            AutoMerge = autoMerge,
            Type = type,
            Status = TicketStatus.Backlog,
            Token = SlugGenerator.NewToken(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(ct);
        await PublishTicketAsync(ticket.Id, ct);
        return ticket;
    }

    public async Task<Ticket> PatchAsync(long id, string? title, string? body, string? spec, bool? autoMerge = null, CancellationToken ct = default)
    {
        var ticket = await LoadAsync(id, ct);
        if (title is not null)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                throw DispatchException.BadRequest("title must not be empty");
            }

            ticket.Title = title.Trim();
        }

        if (body is not null)
        {
            ticket.Body = body;
        }

        if (spec is not null)
        {
            if (!SpecEditable.Contains(ticket.Status))
            {
                throw DispatchException.InvalidTransition(
                    $"spec is only editable in ready/needs_input/backlog (ticket is {EnumNames.ToWire(ticket.Status)})");
            }

            ticket.Spec = spec;
        }

        if (autoMerge is { } am)
        {
            ticket.AutoMerge = am;
        }

        await TouchAndSaveAsync(ticket, ct);
        return ticket;
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        var ticket = await LoadAsync(id, ct);
        var active = await ActiveRunAsync(id, ct);
        if (active is not null)
        {
            await CancelRunInternalAsync(ticket, active, markTicketFailed: false, ct);
        }

        if (ticket.Container is not null)
        {
            await incus.DeleteAsync(ticket.Container, snapshot: false, ct);
        }
        else
        {
            var state = await incus.GetStateAsync(ticket.ContainerName, ct);
            if (state is ContainerState.Running or ContainerState.Stopped)
            {
                await incus.DeleteAsync(ticket.ContainerName, snapshot: false, ct);
            }
        }

        db.Tickets.Remove(ticket);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Comment> AddCommentAsync(long id, CommentAuthor author, string? text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw DispatchException.BadRequest("text is required");
        }

        var ticket = await LoadAsync(id, ct);
        var comment = new Comment { TicketId = ticket.Id, Author = author, Text = text, CreatedAt = DateTime.UtcNow };
        db.Comments.Add(comment);
        await TouchAndSaveAsync(ticket, ct);
        return comment;
    }

    // ---- transitions --------------------------------------------------------

    public async Task<Run> RefineAsync(long id, CancellationToken ct = default)
    {
        var ticket = await LoadAsync(id, ct);
        await EnsureNoActiveRunAsync(ticket, ct);

        switch (ticket.Status)
        {
            case TicketStatus.Backlog:
            case TicketStatus.Failed:
                break;
            case TicketStatus.NeedsInput:
                var unanswered = await db.Questions.CountAsync(q => q.TicketId == id && q.Answer == null, ct);
                if (unanswered > 0)
                {
                    throw DispatchException.InvalidTransition($"ticket has {unanswered} unanswered question(s); answer them first");
                }

                break;
            default:
                throw DispatchException.InvalidTransition($"cannot refine from {EnumNames.ToWire(ticket.Status)}");
        }

        var project = registry.Require(ticket.ProjectId);
        var prompt = PromptRenderer.Render(project.Prompts.Refine, ticket, project.Config);
        ticket.Status = TicketStatus.Refining;
        var run = Enqueue(ticket, RunKind.Refine, prompt);
        await TouchAndSaveAsync(ticket, ct);
        await PublishRunAsync(run.Id, ct);
        return run;
    }

    public async Task<Run> AnswerAsync(long id, IReadOnlyList<AnswerItem>? answers, CancellationToken ct = default)
    {
        if (answers is null || answers.Count == 0)
        {
            throw DispatchException.BadRequest("answers must not be empty");
        }

        var ticket = await LoadAsync(id, ct);
        await EnsureNoActiveRunAsync(ticket, ct);
        if (ticket.Status != TicketStatus.NeedsInput)
        {
            throw DispatchException.InvalidTransition($"cannot answer from {EnumNames.ToWire(ticket.Status)}; ticket must be needs_input");
        }

        var questionIds = answers.Select(a => a.QuestionId).ToList();
        var questions = await db.Questions.Where(q => q.TicketId == id && questionIds.Contains(q.Id)).ToListAsync(ct);
        var now = DateTime.UtcNow;
        var answered = new List<Question>();
        foreach (var a in answers)
        {
            var q = questions.FirstOrDefault(x => x.Id == a.QuestionId)
                    ?? throw DispatchException.NotFound("Question", a.QuestionId);
            if (string.IsNullOrWhiteSpace(a.Answer))
            {
                throw DispatchException.BadRequest($"answer for question {a.QuestionId} is empty");
            }

            q.Answer = a.Answer;
            q.AnsweredAt = now;
            answered.Add(q);
        }

        var project = registry.Require(ticket.ProjectId);
        var prompt = PromptRenderer.Render(project.Prompts.Answer, ticket, project.Config, answers: answered);
        if (ticket.ClaudeSessionId is null)
        {
            // No session to resume: start fresh with the refine prompt followed by the answers.
            prompt = PromptRenderer.Render(project.Prompts.Refine, ticket, project.Config) + "\n\n" + prompt;
        }

        ticket.Status = TicketStatus.Refining;
        var run = Enqueue(ticket, RunKind.Answer, prompt);
        await TouchAndSaveAsync(ticket, ct);
        await PublishRunAsync(run.Id, ct);
        return run;
    }

    public async Task<Run> StartAsync(long id, CancellationToken ct = default)
    {
        var ticket = await LoadAsync(id, ct);
        await EnsureNoActiveRunAsync(ticket, ct);
        var taskShortcut = ticket.Type == TicketType.Task && ticket.Status is TicketStatus.Backlog or TicketStatus.Failed;
        if (ticket.Status != TicketStatus.Ready && !taskShortcut)
        {
            throw DispatchException.InvalidTransition(ticket.Type == TicketType.Task
                ? $"cannot start a task from {EnumNames.ToWire(ticket.Status)}; it must be backlog, failed or ready"
                : $"cannot start from {EnumNames.ToWire(ticket.Status)}; ticket must be ready");
        }

        if (taskShortcut && string.IsNullOrWhiteSpace(ticket.Spec))
        {
            ticket.Spec = TaskSpec(ticket);
        }

        if (string.IsNullOrWhiteSpace(ticket.Spec))
        {
            throw DispatchException.InvalidTransition("ticket has no spec");
        }

        var project = registry.Require(ticket.ProjectId);
        ticket.Slug ??= SlugGenerator.FromTitle(ticket.Title);
        await TouchAndSaveAsync(ticket, ct);

        await incus.EnsureTicketContainerAsync(ticket, project, ct);
        ticket.Container = ticket.ContainerName;

        var orchestrator = project.Config.OrchestratorDir;
        var featureFile = $"features/{ticket.Id}-{ticket.Slug}.md";
        await incus.PushFileAsync(ticket.Container, $"{orchestrator}/{featureFile}", ticket.Spec!, ct: ct);
        await CommitFeatureFileAsync(ticket, orchestrator, featureFile, ct);

        var prompt = PromptRenderer.Render(project.Prompts.Work, ticket, project.Config);
        if (ticket.Type == TicketType.Task)
        {
            // No refinement happened: plan -> implement -> review -> fix all run in this one session.
            prompt = PromptRenderer.Render(project.Prompts.Plan, ticket, project.Config) + "\n\n" + prompt;
        }

        ticket.Status = TicketStatus.InProgress;
        var run = Enqueue(ticket, RunKind.Work, prompt);
        await TouchAndSaveAsync(ticket, ct);
        await PublishRunAsync(run.Id, ct);
        return run;
    }

    /// <summary>Auto-start right after creation. A failure (container, git) must not lose the new ticket: it stays in backlog with a system comment.</summary>
    public async Task StartCreatedTaskAsync(long id, CancellationToken ct = default)
    {
        try
        {
            await StartAsync(id, ct);
        }
        catch (DispatchException ex)
        {
            logger.LogWarning(ex, "Auto-start of task ticket {TicketId} failed", id);
            await AddCommentAsync(id, CommentAuthor.System, $"Auto-start failed: {ex.Message}. Press Start work to retry.", ct);
        }
    }

    /// <summary>Human-triggered ship: only from review with a PASSED gate.</summary>
    public async Task<Run> ShipAsync(long id, CancellationToken ct = default)
    {
        var ticket = await LoadAsync(id, ct);
        await EnsureNoActiveRunAsync(ticket, ct);
        if (ticket.Status != TicketStatus.Review)
        {
            throw DispatchException.InvalidTransition($"cannot ship from {EnumNames.ToWire(ticket.Status)}; ticket must be review");
        }

        if (!string.Equals(RunOutcome.ReadGate(ticket.WorkflowState), "PASSED", StringComparison.OrdinalIgnoreCase))
        {
            throw DispatchException.InvalidTransition("review gate is not PASSED");
        }

        var run = QueueShip(ticket);
        await TouchAndSaveAsync(ticket, ct);
        await PublishRunAsync(run.Id, ct);
        return run;
    }

    /// <summary>Queues a ship run and moves the ticket to in_progress. Does not save; callers own the unit of work.</summary>
    public Run QueueShip(Ticket ticket)
    {
        var project = registry.Require(ticket.ProjectId);
        var prompt = PromptRenderer.Render(project.Prompts.Ship, ticket, project.Config);
        ticket.Status = TicketStatus.InProgress;
        return Enqueue(ticket, RunKind.Ship, prompt);
    }

    /// <summary>After a successful ship run: drop the container and close the ticket. Does not save.</summary>
    public async Task CompleteShippedAsync(Ticket ticket, CancellationToken ct = default)
    {
        var container = ticket.Container ?? ticket.ContainerName;
        try
        {
            var state = await incus.GetStateAsync(container, ct);
            if (state is ContainerState.Running or ContainerState.Stopped)
            {
                await incus.DeleteAsync(container, snapshot: false, ct);
            }

            ticket.Container = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ticket {TicketId} shipped but container {Container} could not be deleted", ticket.Id, container);
        }

        ticket.Status = TicketStatus.Done;
    }

    public async Task<Run> ResumeAsync(long id, string? message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw DispatchException.BadRequest("message is required");
        }

        var ticket = await LoadAsync(id, ct);
        await EnsureNoActiveRunAsync(ticket, ct);
        if (ticket.ClaudeSessionId is null)
        {
            throw DispatchException.InvalidTransition("ticket has no claude session to resume");
        }

        if (ticket.Status == TicketStatus.Done)
        {
            throw DispatchException.InvalidTransition("cannot resume a done ticket");
        }

        var run = Enqueue(ticket, RunKind.Resume, message);
        await TouchAndSaveAsync(ticket, ct);
        await PublishRunAsync(run.Id, ct);
        return run;
    }

    public async Task<Run> CancelAsync(long id, CancellationToken ct = default)
    {
        var ticket = await LoadAsync(id, ct);
        var active = await ActiveRunAsync(id, ct)
                     ?? throw DispatchException.InvalidTransition("ticket has no active run");
        await CancelRunInternalAsync(ticket, active, markTicketFailed: true, ct);
        return active;
    }

    public async Task<Ticket> DoneAsync(long id, bool snapshot, CancellationToken ct = default)
    {
        var ticket = await LoadAsync(id, ct);
        if (ticket.Status is not (TicketStatus.Review or TicketStatus.InProgress or TicketStatus.Failed))
        {
            throw DispatchException.InvalidTransition($"cannot mark done from {EnumNames.ToWire(ticket.Status)}");
        }

        var active = await ActiveRunAsync(id, ct);
        if (active is not null)
        {
            await CancelRunInternalAsync(ticket, active, markTicketFailed: false, ct);
        }

        var container = ticket.Container ?? ticket.ContainerName;
        var state = await incus.GetStateAsync(container, ct);
        if (state is ContainerState.Running or ContainerState.Stopped)
        {
            await incus.DeleteAsync(container, snapshot, ct);
        }

        ticket.Container = null;
        ticket.Status = TicketStatus.Done;
        await TouchAndSaveAsync(ticket, ct);
        return ticket;
    }

    public async Task<Ticket> MoveAsync(long id, string? statusText, CancellationToken ct = default)
    {
        var target = EnumNames.Parse<TicketStatus>(statusText);
        var ticket = await LoadAsync(id, ct);
        var active = await ActiveRunAsync(id, ct);
        if (active is not null)
        {
            throw DispatchException.RunActive(id);
        }

        if (RunningStates.Contains(ticket.Status) || RunningStates.Contains(target))
        {
            throw DispatchException.InvalidTransition(
                $"move is only allowed between non-running states ({EnumNames.ToWire(ticket.Status)} -> {EnumNames.ToWire(target)})");
        }

        ticket.Status = target;
        await TouchAndSaveAsync(ticket, ct);
        return ticket;
    }

    public async Task<Ticket> RefreshFromContainerAsync(long id, CancellationToken ct = default)
    {
        var ticket = await LoadAsync(id, ct);
        await RefreshFromContainerAsync(ticket, ct);
        await TouchAndSaveAsync(ticket, ct);
        return ticket;
    }

    /// <summary>Pulls tasks/&lt;id&gt;/state.json and result.json; does not save.</summary>
    public async Task RefreshFromContainerAsync(Ticket ticket, CancellationToken ct = default)
    {
        var container = ticket.Container ?? ticket.ContainerName;
        var project = registry.GetById(ticket.ProjectId);
        if (project is null)
        {
            return;
        }

        var baseDir = $"{project.Config.OrchestratorDir}/tasks/{ticket.Id}";
        var state = await incus.PullFileAsync(container, $"{baseDir}/state.json", ct);
        var result = await incus.PullFileAsync(container, $"{baseDir}/result.json", ct);

        if (TryParse(state, out var stateDoc))
        {
            ticket.WorkflowState = stateDoc;
        }

        if (TryParse(result, out var resultDoc))
        {
            ticket.Result = resultDoc;
        }

        // Manual refresh only: while a run is active (including a ship run) the queue decides the status when it ends.
        if (ticket.Status == TicketStatus.InProgress
            && string.Equals(RunOutcome.ReadGate(ticket.WorkflowState), "PASSED", StringComparison.OrdinalIgnoreCase)
            && await ActiveRunAsync(ticket.Id, ct) is null)
        {
            ticket.Status = TicketStatus.Review;
        }
    }

    // ---- CLI-side mutations ---------------------------------------------------

    public async Task<List<Question>> AddQuestionsAsync(long id, IReadOnlyList<string>? texts, CancellationToken ct = default)
    {
        var cleaned = (texts ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
        if (cleaned.Count == 0)
        {
            throw DispatchException.BadRequest("questions must not be empty");
        }

        var ticket = await LoadAsync(id, ct);
        var active = await ActiveRunAsync(id, ct);
        var now = DateTime.UtcNow;
        var created = cleaned.Select(text => new Question
        {
            TicketId = ticket.Id,
            Text = text,
            AskedAt = now,
            RunId = active?.Id,
        }).ToList();
        db.Questions.AddRange(created);

        if (active is null && ticket.Status is not (TicketStatus.Done or TicketStatus.InProgress or TicketStatus.Review))
        {
            // No run to wait for: apply the outcome rule immediately.
            ticket.Status = TicketStatus.NeedsInput;
        }

        await TouchAndSaveAsync(ticket, ct);
        return created;
    }

    public async Task<Ticket> SubmitSpecAsync(long id, string? spec, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            throw DispatchException.BadRequest("spec is required");
        }

        var ticket = await LoadAsync(id, ct);
        var active = await ActiveRunAsync(id, ct);
        ticket.Spec = spec;

        if (active is not null)
        {
            active.SpecSubmitted = true;
        }
        else if (ticket.Status is TicketStatus.Backlog or TicketStatus.Refining or TicketStatus.NeedsInput or TicketStatus.Failed)
        {
            ticket.Status = TicketStatus.Ready;
        }

        await TouchAndSaveAsync(ticket, ct);
        return ticket;
    }

    public async Task AddProgressAsync(long id, string? phase, string? note, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(phase))
        {
            throw DispatchException.BadRequest("phase is required");
        }

        var ticket = await LoadAsync(id, ct);
        var active = await ActiveRunAsync(id, ct);
        db.ProgressNotes.Add(new ProgressNote
        {
            TicketId = ticket.Id,
            RunId = active?.Id,
            Phase = phase.Trim(),
            Note = string.IsNullOrWhiteSpace(note) ? null : note,
            CreatedAt = DateTime.UtcNow,
        });
        await TouchAndSaveAsync(ticket, ct);
    }

    // ---- helpers --------------------------------------------------------------

    public async Task<Ticket> LoadAsync(long id, CancellationToken ct = default) =>
        await db.Tickets.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw DispatchException.NotFound("Ticket", id);

    public Task<Run?> ActiveRunAsync(long ticketId, CancellationToken ct = default) =>
        db.Runs.FirstOrDefaultAsync(r => r.TicketId == ticketId && (r.Status == RunStatus.Pending || r.Status == RunStatus.Running), ct);

    private async Task EnsureNoActiveRunAsync(Ticket ticket, CancellationToken ct)
    {
        if (await ActiveRunAsync(ticket.Id, ct) is not null)
        {
            throw DispatchException.RunActive(ticket.Id);
        }
    }

    private Run Enqueue(Ticket ticket, RunKind kind, string prompt)
    {
        var run = new Run
        {
            TicketId = ticket.Id,
            Kind = kind,
            Status = RunStatus.Pending,
            Prompt = prompt,
            CreatedAt = DateTime.UtcNow,
        };
        db.Runs.Add(run);
        return run;
    }

    private async Task CancelRunInternalAsync(Ticket ticket, Run run, bool markTicketFailed, CancellationToken ct)
    {
        logger.LogInformation("Cancelling run {RunId} (ticket {TicketId})", run.Id, ticket.Id);
        run.Status = RunStatus.Cancelled;
        run.EndedAt = DateTime.UtcNow;
        run.Error ??= "cancelled by user";
        if (markTicketFailed)
        {
            ticket.Status = TicketStatus.Failed;
        }

        await TouchAndSaveAsync(ticket, ct);
        cancellations.Cancel(run.Id);
        await PublishRunAsync(run.Id, ct);
    }

    private async Task CommitFeatureFileAsync(Ticket ticket, string orchestratorDir, string featureFile, CancellationToken ct)
    {
        var container = ticket.Container!;
        var identity = await incus.ExecAsync(container, ["git", "config", "user.email"], orchestratorDir, ct: ct);
        var gitArgs = new List<string> { "git" };
        if (!identity.Success || string.IsNullOrWhiteSpace(identity.Stdout))
        {
            gitArgs.AddRange(["-c", "user.name=Dispatch", "-c", "user.email=dispatch@localhost"]);
        }

        var add = await incus.ExecAsync(container, [.. gitArgs, "add", featureFile], orchestratorDir, ct: ct);
        if (!add.Success)
        {
            throw new DispatchException("git_failed", $"git add failed: {add.Stderr.Trim()}", 502);
        }

        var commit = await incus.ExecAsync(container, [.. gitArgs, "commit", "-m", $"feat({ticket.Id}): {ticket.Slug}"], orchestratorDir, ct: ct);
        if (!commit.Success)
        {
            var output = commit.Stdout + commit.Stderr;
            if (!output.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
            {
                throw new DispatchException("git_failed", $"git commit failed: {output.Trim()}", 502);
            }

            logger.LogInformation("Feature file for ticket {TicketId} unchanged; nothing to commit", ticket.Id);
        }

        // Another ticket (or a human) may have pushed to main since this container was created:
        // integrate remote changes first so the push is a fast-forward.
        var pull = await incus.ExecAsync(container, [.. gitArgs, "pull", "--rebase"], orchestratorDir, ct: ct);
        if (!pull.Success)
        {
            await incus.ExecAsync(container, ["git", "rebase", "--abort"], orchestratorDir, ct: ct);
            throw new DispatchException("git_failed", $"git pull --rebase failed: {(pull.Stdout + pull.Stderr).Trim()}", 502);
        }

        var push = await incus.ExecAsync(container, [.. gitArgs, "push"], orchestratorDir, ct: ct);
        if (!push.Success)
        {
            throw new DispatchException("git_failed", $"git push failed: {push.Stderr.Trim()}", 502);
        }
    }

    /// <summary>Tasks skip refinement: the feature file starts as the human's request; the plan prompt has the agent rewrite it into a plan.</summary>
    public static string TaskSpec(Ticket ticket)
    {
        var body = string.IsNullOrWhiteSpace(ticket.Body) ? "(no further description: the title says it all)" : ticket.Body.Trim();
        return $"# {ticket.Title}\n\n" +
               "_Task ticket, not planned yet. The text below is the human's request as written; " +
               "the work run rewrites this file into a plan before implementing._\n\n" +
               body + "\n";
    }

    private static bool TryParse(string? json, out JsonDocument? doc)
    {
        doc = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            doc = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task TouchAndSaveAsync(Ticket ticket, CancellationToken ct)
    {
        ticket.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await PublishTicketAsync(ticket.Id, ct);
    }

    private async Task PublishTicketAsync(long ticketId, CancellationToken ct)
    {
        if (bus.SubscriberCount == 0)
        {
            return;
        }

        try
        {
            bus.PublishTicket(await mapper.TicketAsync(ticketId, ct));
        }
        catch (DispatchException)
        {
            // ticket deleted meanwhile
        }
    }

    private async Task PublishRunAsync(long runId, CancellationToken ct)
    {
        queueSignal.Wake();
        if (bus.SubscriberCount > 0)
        {
            bus.PublishRun(await mapper.RunAsync(runId, ct));
        }
    }
}
