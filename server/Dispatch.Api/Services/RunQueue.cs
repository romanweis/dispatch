using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Dispatch.Api.Config;
using Dispatch.Api.Data;
using Dispatch.Api.Incus;
using Dispatch.Claude;
using Dispatch.Claude.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dispatch.Api.Services;

/// <summary>Polls pending runs, respects per-project MaxParallel, executes claude and records events.</summary>
public sealed class RunQueue(
    IServiceScopeFactory scopeFactory,
    IProjectRegistry registry,
    IIncusService incus,
    IClaudeLauncher launcher,
    EventBus bus,
    RunQueueSignal signal,
    RunCancellationRegistry cancellations,
    IOptions<DispatchOptions> options,
    ILogger<RunQueue> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(30);
    private const int FlushBatchSize = 50;

    private readonly ConcurrentDictionary<long, Task> _inFlight = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverInterruptedRunsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Run queue tick failed");
            }

            await signal.WaitAsync(PollInterval, stoppingToken);
        }

        await Task.WhenAll(_inFlight.Values.ToArray()).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ContinueWith(_ => { });
    }

    private async Task RecoverInterruptedRunsAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();
        var stale = await db.Runs.Include(r => r.Ticket).Where(r => r.Status == RunStatus.Running).ToListAsync(ct);
        foreach (var run in stale)
        {
            logger.LogWarning("Run {RunId} was running when the server stopped; marking failed", run.Id);
            run.Status = RunStatus.Failed;
            run.EndedAt = DateTime.UtcNow;
            run.Error = "interrupted by server restart";
            if (run.Ticket is not null && run.Ticket.Status is TicketStatus.Refining or TicketStatus.InProgress)
            {
                run.Ticket.Status = TicketStatus.Failed;
                run.Ticket.UpdatedAt = DateTime.UtcNow;
            }
        }

        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task DispatchPendingAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();

        var pending = await db.Runs
            .Include(r => r.Ticket)
            .Where(r => r.Status == RunStatus.Pending)
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .ToListAsync(ct);
        if (pending.Count == 0)
        {
            return;
        }

        var runningPerProject = await db.Runs
            .Where(r => r.Status == RunStatus.Running)
            .GroupBy(r => r.Ticket!.ProjectId)
            .Select(g => new { ProjectId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ProjectId, x => x.Count, ct);

        foreach (var run in pending)
        {
            if (run.Ticket is null || _inFlight.ContainsKey(run.Id))
            {
                continue;
            }

            var project = registry.GetById(run.Ticket.ProjectId);
            if (project is null)
            {
                run.Status = RunStatus.Failed;
                run.EndedAt = DateTime.UtcNow;
                run.Error = $"project {run.Ticket.ProjectId} is not loaded";
                run.Ticket.Status = TicketStatus.Failed;
                await db.SaveChangesAsync(ct);
                continue;
            }

            var running = runningPerProject.GetValueOrDefault(project.Id);
            if (running >= project.Config.MaxParallel)
            {
                continue;
            }

            run.Status = RunStatus.Running;
            run.StartedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            runningPerProject[project.Id] = running + 1;

            var runId = run.Id;
            var task = Task.Run(() => ExecuteRunAsync(runId, CancellationToken.None), CancellationToken.None);
            _inFlight[runId] = task;
            _ = task.ContinueWith(_ => { _inFlight.TryRemove(runId, out var _); }, TaskScheduler.Default);
        }
    }

    internal async Task ExecuteRunAsync(long runId, CancellationToken hostCt)
    {
        var runToken = cancellations.Register(runId);
        var sw = Stopwatch.StartNew();
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();
            var tickets = scope.ServiceProvider.GetRequiredService<TicketService>();
            var mapper = scope.ServiceProvider.GetRequiredService<DtoMapper>();

            var run = await db.Runs.Include(r => r.Ticket).FirstAsync(r => r.Id == runId, hostCt);
            var ticket = run.Ticket!;
            var project = registry.Require(ticket.ProjectId);

            bus.PublishRun(await mapper.RunAsync(runId, hostCt));
            logger.LogInformation("Run {RunId} ({Kind}) for ticket {TicketId} starting", runId, run.Kind, ticket.Id);

            try
            {
                await ExecuteCoreAsync(db, tickets, mapper, run, ticket, project, runToken);
            }
            catch (OperationCanceledException) when (runToken.IsCancellationRequested)
            {
                logger.LogInformation("Run {RunId} cancelled", runId);
                await ReloadAndFinishAsync(db, run, ticket, RunStatus.Cancelled, null, "cancelled", TicketStatus.Failed, hostCt);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Run {RunId} failed", runId);
                await ReloadAndFinishAsync(db, run, ticket, RunStatus.Failed, null, ex.Message, TicketStatus.Failed, hostCt);
            }

            bus.PublishRun(await mapper.RunAsync(runId, hostCt));
            bus.PublishTicket(await mapper.TicketAsync(ticket.Id, hostCt));
            logger.LogInformation("Run {RunId} finished as {Status} after {Seconds:F1}s", runId, run.Status, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Run {RunId}: unrecoverable failure while finalising", runId);
        }
        finally
        {
            cancellations.Complete(runId);
            signal.Wake();
        }
    }

    private async Task ExecuteCoreAsync(
        DispatchDbContext db,
        TicketService tickets,
        DtoMapper mapper,
        Run run,
        Ticket ticket,
        LoadedProject project,
        CancellationToken ct)
    {
        await incus.EnsureTicketContainerAsync(ticket, project, ct);
        ticket.Container = ticket.ContainerName;
        ticket.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var sessionOptions = BuildSessionOptions(run, ticket, project);
        await using var claude = await launcher.LaunchAsync(
            sessionOptions,
            line => logger.LogDebug("claude[{RunId}] stderr: {Line}", run.Id, line),
            ct);

        var seq = await db.RunEvents.Where(e => e.RunId == run.Id).MaxAsync(e => (long?)e.Seq, ct) ?? 0;
        var batch = new List<RunEvent>();
        var lastFlush = Stopwatch.StartNew();
        var resultSeen = false;
        var resultIsError = false;
        var sessionStored = run.SessionId is not null;

        async Task FlushAsync()
        {
            if (batch.Count == 0)
            {
                return;
            }

            db.RunEvents.AddRange(batch);
            await db.SaveChangesAsync(CancellationToken.None);
            foreach (var e in batch)
            {
                bus.PublishRunEvent(run.Id, DtoMapper.ToDto(e));
            }

            batch.Clear();
            lastFlush.Restart();
        }

        await using var enumerator = claude.Events.GetAsyncEnumerator(ct);
        while (true)
        {
            var moveNext = enumerator.MoveNextAsync().AsTask();
            if (batch.Count > 0)
            {
                // Time-based flush: do not hold events back while waiting for the next one.
                var remaining = FlushInterval - lastFlush.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.WhenAny(moveNext, Task.Delay(remaining, ct));
                }

                if (!moveNext.IsCompleted)
                {
                    await FlushAsync();
                }
            }

            if (!await moveNext)
            {
                break;
            }

            var evt = enumerator.Current;
            seq++;
            batch.Add(new RunEvent
            {
                RunId = run.Id,
                Seq = seq,
                Payload = JsonDocument.Parse(evt.Raw.GetRawText()),
                RecordedAt = DateTime.UtcNow,
            });

            if (!sessionStored)
            {
                var sid = claude.CapturedSessionId ?? ReadSessionId(evt.Raw);
                if (!string.IsNullOrEmpty(sid))
                {
                    run.SessionId = sid;
                    ticket.ClaudeSessionId = sid;
                    sessionStored = true;
                }
            }

            if (evt is ResultEvent result)
            {
                resultSeen = true;
                resultIsError = result.IsError;
                await FlushAsync();
                break;
            }

            if (batch.Count >= FlushBatchSize || lastFlush.Elapsed >= FlushInterval)
            {
                await FlushAsync();
            }
        }

        await FlushAsync();

        // Close stdin so claude exits; wait for the exit code with a grace period.
        await claude.DisposeAsync();
        int exitCode;
        try
        {
            exitCode = await claude.ProcessExited.WaitAsync(ExitGrace, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Run {RunId}: claude did not exit within {Grace}s after events completed", run.Id, ExitGrace.TotalSeconds);
            exitCode = resultSeen && !resultIsError ? 0 : -1;
        }

        if (resultSeen && !resultIsError && exitCode != 0)
        {
            // A clean result followed by a forced shutdown (stdin close + kill) is still a successful run.
            logger.LogDebug("Run {RunId}: result received, treating exit code {Exit} as success", run.Id, exitCode);
            exitCode = 0;
        }

        ct.ThrowIfCancellationRequested();

        // Reload run facts written by the CLI endpoints during the run (other DbContexts).
        var newQuestions = await db.Questions.AnyAsync(q => q.RunId == run.Id, CancellationToken.None);
        var specSubmitted = await db.Runs.Where(r => r.Id == run.Id).Select(r => r.SpecSubmitted).FirstAsync(CancellationToken.None);
        var currentStatus = await db.Tickets.Where(t => t.Id == ticket.Id).Select(t => t.Status).FirstAsync(CancellationToken.None);
        run.SpecSubmitted = specSubmitted;

        var refreshedTicket = await db.Tickets.AsNoTracking().FirstAsync(t => t.Id == ticket.Id, CancellationToken.None);

        // Copy fields written concurrently (CLI endpoints use their own DbContext) onto our tracked instance, keeping our session id.
        ticket.Spec = refreshedTicket.Spec;
        ticket.Status = currentStatus;
        ticket.WorkflowState = refreshedTicket.WorkflowState;
        ticket.Result = refreshedTicket.Result;
        ticket.Slug = refreshedTicket.Slug;

        var refinement = run.Kind is RunKind.Refine or RunKind.Answer
                         || (run.Kind == RunKind.Resume && currentStatus is not (TicketStatus.InProgress or TicketStatus.Review or TicketStatus.Done));
        if (!refinement)
        {
            await tickets.RefreshFromContainerAsync(ticket, CancellationToken.None);
        }

        var facts = new RunFacts(
            run.Kind,
            currentStatus,
            exitCode,
            resultSeen,
            resultIsError,
            newQuestions,
            specSubmitted,
            RunOutcome.ReadGate(ticket.WorkflowState));

        var success = facts.Succeeded;
        run.Status = success ? RunStatus.Done : RunStatus.Failed;
        run.ExitCode = exitCode;
        run.EndedAt = DateTime.UtcNow;
        run.Error = success ? null : (resultSeen ? (resultIsError ? "claude reported an error result" : $"exit code {exitCode}") : "claude exited without a result event");

        if (currentStatus != TicketStatus.Failed || run.Status == RunStatus.Done)
        {
            ticket.Status = RunOutcome.Decide(facts);
        }

        ticket.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static async Task ReloadAndFinishAsync(
        DispatchDbContext db,
        Run run,
        Ticket ticket,
        RunStatus status,
        int? exitCode,
        string error,
        TicketStatus ticketStatus,
        CancellationToken ct)
    {
        // The run may already have been marked cancelled by TicketService.CancelAsync (different DbContext).
        var persisted = await db.Runs.AsNoTracking().Where(r => r.Id == run.Id).Select(r => r.Status).FirstAsync(ct);
        run.Status = persisted is RunStatus.Cancelled ? RunStatus.Cancelled : status;
        run.EndedAt ??= DateTime.UtcNow;
        run.ExitCode ??= exitCode;
        run.Error ??= error;
        var persistedTicket = await db.Tickets.AsNoTracking().Where(t => t.Id == ticket.Id).Select(t => t.Status).FirstAsync(ct);
        ticket.Status = persistedTicket == TicketStatus.Done ? TicketStatus.Done : ticketStatus;
        ticket.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    internal ClaudeAgentSessionOptions BuildSessionOptions(Run run, Ticket ticket, LoadedProject project)
    {
        var cfg = project.Config;
        var env = incus.BuildTicketEnvironment(ticket, project);
        var cwd = cfg.OrchestratorDir;

        var extra = new List<string>
        {
            "--max-turns",
            (run.Kind == RunKind.Work ? cfg.Claude.MaxTurnsWork : cfg.Claude.MaxTurnsRefine).ToString(),
        };
        if (!string.IsNullOrWhiteSpace(cfg.Claude.Model))
        {
            extra.AddRange(["--model", cfg.Claude.Model]);
        }

        // answer/work/resume continue the ticket's session; refine only does so when one already exists.
        var resume = ticket.ClaudeSessionId;

        var prefix = incus.BuildClaudePrefix(ticket.ContainerName, cwd, env);

        return new ClaudeAgentSessionOptions
        {
            WorkingDirectory = ".",
            ExecutablePrefix = options.Value.FakeIncus || prefix.Count == 0 ? null : prefix,
            InitialPrompt = run.Prompt,
            ResumeSessionId = string.IsNullOrWhiteSpace(resume) ? null : resume,
            PermissionMode = string.IsNullOrWhiteSpace(cfg.Claude.PermissionMode) ? null : cfg.Claude.PermissionMode,
            AllowedTools = cfg.Claude.AllowedTools.Count > 0 ? cfg.Claude.AllowedTools : null,
            AdditionalArgs = extra,
            GracefulShutdown = TimeSpan.FromSeconds(10),
        };
    }

    private static string? ReadSessionId(JsonElement raw) =>
        raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String
            ? sid.GetString()
            : null;
}
