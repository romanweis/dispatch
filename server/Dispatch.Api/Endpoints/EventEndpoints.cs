using System.Threading.Channels;
using Dispatch.Api.Services;

namespace Dispatch.Api.Endpoints;

public static class EventEndpoints
{
    public static RouteGroupBuilder MapEventEndpoints(this RouteGroupBuilder api)
    {
        // Board feed: event `ticket` (TicketDto) and event `run` (RunDto).
        api.MapGet("/events", async (HttpContext http, EventBus bus) =>
        {
            var ct = http.RequestAborted;
            using var subscription = bus.Subscribe();
            var sse = SseWriter.Start(http);

            try
            {
                await sse.PingAsync(ct);
                while (!ct.IsCancellationRequested)
                {
                    using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    heartbeat.CancelAfter(SseWriter.HeartbeatInterval);
                    BusMessage message;
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

                    switch (message.Kind)
                    {
                        case BusMessageKind.Ticket:
                            await sse.SendAsync("ticket", message.Payload, ct);
                            break;
                        case BusMessageKind.Run:
                            await sse.SendAsync("run", message.Payload, ct);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // client disconnected
            }
        });

        return api;
    }
}
