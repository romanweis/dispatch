using System.Threading.Channels;
using Dispatch.Api.Data;
using Dispatch.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Api.Endpoints;

public static class RunEndpoints
{
    public static RouteGroupBuilder MapRunEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/runs");

        group.MapGet("/{id:long}", async (long id, DtoMapper mapper, CancellationToken ct) =>
            Results.Ok(await mapper.RunAsync(id, ct)));

        group.MapGet("/{id:long}/events", async (long id, long? since, int? limit, DispatchDbContext db, CancellationToken ct) =>
        {
            if (!await db.Runs.AnyAsync(r => r.Id == id, ct))
            {
                throw DispatchException.NotFound("Run", id);
            }

            var take = Math.Clamp(limit ?? 500, 1, 5000);
            var from = since ?? 0;
            var events = await db.RunEvents.AsNoTracking()
                .Where(e => e.RunId == id && e.Seq > from)
                .OrderBy(e => e.Seq)
                .Take(take)
                .ToListAsync(ct);
            return Results.Ok(events.Select(DtoMapper.ToDto).ToList());
        });

        group.MapGet("/{id:long}/stream", StreamRunAsync);

        return group;
    }

    /// <summary>Replays events from ?since (default 0) then streams live; ends with `run_done` once the run is terminal.</summary>
    private static async Task StreamRunAsync(long id, long? since, HttpContext http, DispatchDbContext db, DtoMapper mapper, EventBus bus)
    {
        var ct = http.RequestAborted;
        if (!await db.Runs.AnyAsync(r => r.Id == id, ct))
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            await http.Response.WriteAsJsonAsync(new ErrorDto($"Run {id} not found", "not_found"), ct);
            return;
        }

        // Subscribe before replaying so nothing is lost between the two phases; dedupe by seq.
        using var subscription = bus.Subscribe();
        var sse = SseWriter.Start(http);
        var lastSeq = since ?? 0;

        try
        {
            while (true)
            {
                var page = await db.RunEvents.AsNoTracking()
                    .Where(e => e.RunId == id && e.Seq > lastSeq)
                    .OrderBy(e => e.Seq)
                    .Take(500)
                    .ToListAsync(ct);
                if (page.Count == 0)
                {
                    break;
                }

                foreach (var e in page)
                {
                    await sse.SendAsync("run_event", DtoMapper.ToDto(e), ct);
                    lastSeq = e.Seq;
                }
            }

            var run = await mapper.RunAsync(id, ct);
            if (run.Status is not (RunStatus.Pending or RunStatus.Running))
            {
                await sse.SendAsync("run_done", run, ct);
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                heartbeat.CancelAfter(SseWriter.HeartbeatInterval);
                BusMessage? message = null;
                try
                {
                    message = await subscription.Reader.ReadAsync(heartbeat.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await sse.PingAsync(ct);
                    continue;
                }
                catch (ChannelClosedException)
                {
                    return;
                }

                if (message.RunId != id)
                {
                    continue;
                }

                switch (message)
                {
                    case { Kind: BusMessageKind.RunEvent, Payload: RunEventDto evt }:
                        if (evt.Seq > lastSeq)
                        {
                            lastSeq = evt.Seq;
                            await sse.SendAsync("run_event", evt, ct);
                        }

                        break;
                    case { Kind: BusMessageKind.Run, Payload: RunDto runDto }
                        when runDto.Status is not (RunStatus.Pending or RunStatus.Running):
                        // Catch up anything flushed after our last live event before closing.
                        var tail = await db.RunEvents.AsNoTracking()
                            .Where(e => e.RunId == id && e.Seq > lastSeq)
                            .OrderBy(e => e.Seq)
                            .ToListAsync(ct);
                        foreach (var e in tail)
                        {
                            lastSeq = e.Seq;
                            await sse.SendAsync("run_event", DtoMapper.ToDto(e), ct);
                        }

                        await sse.SendAsync("run_done", runDto, ct);
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // client disconnected
        }
    }
}
