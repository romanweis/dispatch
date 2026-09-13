using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Dispatch.Api.Services;

public enum BusMessageKind
{
    Ticket,
    Run,
    RunEvent,
}

/// <summary>Payload is a DTO (TicketDto / RunDto / RunEventDto). RunId is set for Run and RunEvent messages.</summary>
public sealed record BusMessage(BusMessageKind Kind, long? RunId, object Payload);

/// <summary>In-process pub/sub used by the SSE endpoints. One bounded channel per subscriber; slow subscribers drop oldest.</summary>
public sealed class EventBus
{
    private readonly ConcurrentDictionary<Guid, Channel<BusMessage>> _subscribers = new();

    public int SubscriberCount => _subscribers.Count;

    public Subscription Subscribe(int capacity = 4096)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<BusMessage>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _subscribers[id] = channel;
        return new Subscription(this, id, channel.Reader);
    }

    public void Publish(BusMessage message)
    {
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(message);
        }
    }

    public void PublishTicket(TicketDto ticket) => Publish(new BusMessage(BusMessageKind.Ticket, null, ticket));

    public void PublishRun(RunDto run) => Publish(new BusMessage(BusMessageKind.Run, run.Id, run));

    public void PublishRunEvent(long runId, RunEventDto evt) => Publish(new BusMessage(BusMessageKind.RunEvent, runId, evt));

    private void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    public sealed class Subscription(EventBus bus, Guid id, ChannelReader<BusMessage> reader) : IDisposable
    {
        public ChannelReader<BusMessage> Reader { get; } = reader;

        public void Dispose() => bus.Unsubscribe(id);
    }
}
