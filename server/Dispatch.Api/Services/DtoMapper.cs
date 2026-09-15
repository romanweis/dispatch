using System.Text.Json;
using Dispatch.Api.Config;
using Dispatch.Api.Data;
using Dispatch.Api.Incus;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Api.Services;

/// <summary>Builds the API DTOs (needs the DB for counts and incus for container state).</summary>
public sealed class DtoMapper(DispatchDbContext db, IIncusService incus, IProjectRegistry registry)
{
    public static ProjectDto ToDto(Project p) =>
        new(
            p.Id, p.Name, p.DisplayName, p.Org, p.Workspace, p.BaseContainer, p.MaxParallel,
            JsonSerializer.Deserialize<List<string>>(p.ReposJson) ?? [],
            p.CreatedAt);

    public static QuestionDto ToDto(Question q) => new(q.Id, q.TicketId, q.Text, q.Answer, q.AskedAt, q.AnsweredAt);

    public static CommentDto ToDto(Comment c) => new(c.Id, c.TicketId, c.Author, c.Text, c.CreatedAt);

    public static RunDto ToDto(Run r, int eventCount) =>
        new(r.Id, r.TicketId, r.Kind, r.Status, r.SessionId, r.StartedAt, r.EndedAt, r.ExitCode, r.Error, eventCount, r.CreatedAt);

    public static RunEventDto ToDto(RunEvent e) => new(e.Seq, e.RecordedAt, e.Payload.RootElement.Clone());

    public async Task<RunDto> RunAsync(long runId, CancellationToken ct = default)
    {
        var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct)
                  ?? throw DispatchException.NotFound("Run", runId);
        var count = await db.RunEvents.CountAsync(e => e.RunId == runId, ct);
        return ToDto(run, count);
    }

    public async Task<List<RunDto>> RunsForTicketAsync(long ticketId, CancellationToken ct = default)
    {
        var runs = await db.Runs.AsNoTracking()
            .Where(r => r.TicketId == ticketId)
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .ToListAsync(ct);
        var counts = await db.RunEvents
            .Where(e => e.Run!.TicketId == ticketId)
            .GroupBy(e => e.RunId)
            .Select(g => new { RunId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RunId, x => x.Count, ct);
        return runs.Select(r => ToDto(r, counts.GetValueOrDefault(r.Id))).ToList();
    }

    public async Task<TicketDto> TicketAsync(long ticketId, CancellationToken ct = default)
    {
        var list = await TicketsAsync(db.Tickets.Where(t => t.Id == ticketId), ct);
        return list.FirstOrDefault() ?? throw DispatchException.NotFound("Ticket", ticketId);
    }

    public async Task<List<TicketDto>> TicketsAsync(IQueryable<Ticket> query, CancellationToken ct = default)
    {
        var tickets = await query.AsNoTracking().Include(t => t.Project).ToListAsync(ct);
        if (tickets.Count == 0)
        {
            return [];
        }

        var ids = tickets.Select(t => t.Id).ToList();

        var openQuestions = await db.Questions
            .Where(q => ids.Contains(q.TicketId) && q.Answer == null)
            .GroupBy(q => q.TicketId)
            .Select(g => new { TicketId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TicketId, x => x.Count, ct);

        var activeRuns = await db.Runs
            .Where(r => ids.Contains(r.TicketId) && (r.Status == RunStatus.Pending || r.Status == RunStatus.Running))
            .Select(r => new { r.TicketId, r.Id })
            .ToListAsync(ct);
        var activeByTicket = activeRuns.GroupBy(r => r.TicketId).ToDictionary(g => g.Key, g => g.Max(r => r.Id));

        var notes = await db.ProgressNotes
            .Where(n => ids.Contains(n.TicketId))
            .GroupBy(n => n.TicketId)
            .Select(g => g.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id).First())
            .ToListAsync(ct);
        var notesByTicket = notes.ToDictionary(n => n.TicketId);

        var result = new List<TicketDto>(tickets.Count);
        foreach (var t in tickets)
        {
            var containerState = t.Container is null
                ? ContainerState.None
                : await incus.GetStateAsync(t.Container, ct);
            var note = notesByTicket.GetValueOrDefault(t.Id);

            result.Add(new TicketDto(
                t.Id,
                t.ProjectId,
                t.Project?.Name ?? registry.GetById(t.ProjectId)?.Config.Name ?? "",
                t.Title,
                t.Body,
                t.Status,
                t.AutoMerge,
                t.Slug,
                t.Spec,
                t.Container,
                EnumNames.ToWire(containerState),
                t.ClaudeSessionId,
                t.WorkflowState?.RootElement.Clone(),
                t.Result?.RootElement.Clone(),
                openQuestions.GetValueOrDefault(t.Id),
                activeByTicket.TryGetValue(t.Id, out var runId) ? runId : null,
                note is null ? null : new ProgressDto(note.Phase, note.Note, note.CreatedAt),
                t.CreatedAt,
                t.UpdatedAt));
        }

        return result;
    }

    public async Task<TicketDetailDto> TicketDetailAsync(long ticketId, CancellationToken ct = default)
    {
        var ticket = await TicketAsync(ticketId, ct);
        var questions = await db.Questions.AsNoTracking().Where(q => q.TicketId == ticketId).OrderBy(q => q.Id).ToListAsync(ct);
        var comments = await db.Comments.AsNoTracking().Where(c => c.TicketId == ticketId).OrderBy(c => c.Id).ToListAsync(ct);
        var runs = await RunsForTicketAsync(ticketId, ct);
        var project = registry.GetById(ticket.ProjectId);

        return new TicketDetailDto(
            ticket,
            questions.Select(ToDto).ToList(),
            comments.Select(ToDto).ToList(),
            runs,
            BuildAttachCommand(ticket, project));
    }

    public static string BuildAttachCommand(TicketDto ticket, LoadedProject? project)
    {
        var cwd = project?.Config.OrchestratorDir ?? "/home/agent";
        var resume = ticket.ClaudeSessionId is null ? "" : $" --resume {ticket.ClaudeSessionId}";
        return $"incus exec t-{ticket.Id} --user 1000 --group 1000 --cwd {cwd} --env HOME=/home/agent -t -- claude{resume}";
    }
}
