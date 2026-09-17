using Dispatch.Api.Data;
using Dispatch.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Api.Endpoints;

public static class TicketEndpoints
{
    public static RouteGroupBuilder MapTicketEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/tickets");

        group.MapGet("", async (int? projectId, string? status, DispatchDbContext db, DtoMapper mapper, CancellationToken ct) =>
        {
            IQueryable<Ticket> query = db.Tickets;
            if (projectId is { } pid)
            {
                query = query.Where(t => t.ProjectId == pid);
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                var parsed = EnumNames.Parse<TicketStatus>(status);
                query = query.Where(t => t.Status == parsed);
            }

            query = query.OrderByDescending(t => t.UpdatedAt).ThenByDescending(t => t.Id);
            return Results.Ok(await mapper.TicketsAsync(query, ct));
        });

        group.MapPost("", async (CreateTicketRequest body, TicketService tickets, DtoMapper mapper, CancellationToken ct) =>
        {
            var type = body.Type is null ? TicketType.Feature : EnumNames.Parse<TicketType>(body.Type);
            var ticket = await tickets.CreateAsync(body.ProjectId, body.Title, body.Body, body.AutoMerge ?? false, type, ct);
            if (type == TicketType.Task && (body.Start ?? true))
            {
                await tickets.StartCreatedTaskAsync(ticket.Id, ct);
            }

            return Results.Created($"/api/tickets/{ticket.Id}", await mapper.TicketAsync(ticket.Id, ct));
        });

        // Shared with the CLI: bearer optional (validated when present).
        group.MapGet("/{id:long}", async (long id, HttpContext http, DispatchDbContext db, DtoMapper mapper, CancellationToken ct) =>
        {
            await TicketAuth.CheckOptionalAsync(http, id, db, ct);
            return Results.Ok(await mapper.TicketDetailAsync(id, ct));
        });

        group.MapPatch("/{id:long}", async (long id, PatchTicketRequest body, TicketService tickets, DtoMapper mapper, CancellationToken ct) =>
        {
            await tickets.PatchAsync(id, body.Title, body.Body, body.Spec, body.AutoMerge, ct);
            return Results.Ok(await mapper.TicketAsync(id, ct));
        });

        group.MapDelete("/{id:long}", async (long id, TicketService tickets, CancellationToken ct) =>
        {
            await tickets.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        group.MapPost("/{id:long}/refine", async (long id, TicketService tickets, DtoMapper mapper, CancellationToken ct) =>
        {
            var run = await tickets.RefineAsync(id, ct);
            return Results.Accepted($"/api/runs/{run.Id}", await mapper.RunAsync(run.Id, ct));
        });

        group.MapPost("/{id:long}/answer", async (long id, AnswerRequest body, TicketService tickets, DtoMapper mapper, CancellationToken ct) =>
        {
            var run = await tickets.AnswerAsync(id, body.Answers, ct);
            return Results.Accepted($"/api/runs/{run.Id}", await mapper.RunAsync(run.Id, ct));
        });

        group.MapPost("/{id:long}/start", async (long id, TicketService tickets, DtoMapper mapper, CancellationToken ct) =>
        {
            var run = await tickets.StartAsync(id, ct);
            return Results.Accepted($"/api/runs/{run.Id}", await mapper.RunAsync(run.Id, ct));
        });

        group.MapPost("/{id:long}/ship", async (long id, TicketService tickets, DtoMapper mapper, CancellationToken ct) =>
        {
            var run = await tickets.ShipAsync(id, ct);
            return Results.Accepted($"/api/runs/{run.Id}", await mapper.RunAsync(run.Id, ct));
        });

        group.MapPost("/{id:long}/resume", async (long id, ResumeRequest body, TicketService tickets, DtoMapper mapper, CancellationToken ct) =>
        {
            var run = await tickets.ResumeAsync(id, body.Message, ct);
            return Results.Accepted($"/api/runs/{run.Id}", await mapper.RunAsync(run.Id, ct));
        });

        group.MapPost("/{id:long}/cancel", async (long id, TicketService tickets, DtoMapper mapper, CancellationToken ct) =>
        {
            var run = await tickets.CancelAsync(id, ct);
            return Results.Ok(await mapper.RunAsync(run.Id, ct));
        });

        group.MapPost("/{id:long}/done", async (long id, DoneRequest? body, TicketService tickets, DtoMapper mapper, CancellationToken ct) =>
        {
            await tickets.DoneAsync(id, body?.Snapshot ?? false, ct);
            return Results.Ok(await mapper.TicketAsync(id, ct));
        });

        group.MapPost("/{id:long}/move", async (long id, MoveRequest body, TicketService tickets, DtoMapper mapper, CancellationToken ct) =>
        {
            await tickets.MoveAsync(id, body.Status, ct);
            return Results.Ok(await mapper.TicketAsync(id, ct));
        });

        // Shared with the CLI: author is `agent` when a valid bearer token is present, `user` otherwise.
        group.MapPost("/{id:long}/comments", async (long id, CommentRequest body, HttpContext http, DispatchDbContext db, TicketService tickets, CancellationToken ct) =>
        {
            var fromCli = await TicketAuth.CheckOptionalAsync(http, id, db, ct);
            var comment = await tickets.AddCommentAsync(id, fromCli ? CommentAuthor.Agent : CommentAuthor.User, body.Text, ct);
            return Results.Created($"/api/tickets/{id}/comments/{comment.Id}", DtoMapper.ToDto(comment));
        });

        group.MapPost("/{id:long}/container/refresh", async (long id, TicketService tickets, DtoMapper mapper, CancellationToken ct) =>
        {
            await tickets.RefreshFromContainerAsync(id, ct);
            return Results.Ok(await mapper.TicketAsync(id, ct));
        });

        group.MapGet("/{id:long}/runs", async (long id, DispatchDbContext db, DtoMapper mapper, CancellationToken ct) =>
        {
            if (!await db.Tickets.AnyAsync(t => t.Id == id, ct))
            {
                throw DispatchException.NotFound("Ticket", id);
            }

            return Results.Ok(await mapper.RunsForTicketAsync(id, ct));
        });

        // ---- CLI-only (bearer required) ----------------------------------------
        var cli = group.MapGroup("/{id:long}").AddEndpointFilter(TicketAuth.RequireAsync);

        cli.MapPost("/questions", async (long id, QuestionsRequest body, TicketService tickets, CancellationToken ct) =>
        {
            var created = await tickets.AddQuestionsAsync(id, body.Questions, ct);
            return Results.Created($"/api/tickets/{id}", created.Select(DtoMapper.ToDto).ToList());
        });

        cli.MapPost("/spec", async (long id, SpecRequest body, TicketService tickets, DtoMapper mapper, CancellationToken ct) =>
        {
            await tickets.SubmitSpecAsync(id, body.Spec, ct);
            return Results.Ok(await mapper.TicketAsync(id, ct));
        });

        cli.MapPost("/progress", async (long id, ProgressRequest body, TicketService tickets, CancellationToken ct) =>
        {
            await tickets.AddProgressAsync(id, body.Phase, body.Note, ct);
            return Results.NoContent();
        });

        return group;
    }
}
