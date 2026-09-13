using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Dispatch.Api.Services;

/// <summary>Per-run cancellation tokens. CancelAsync triggers the token; the run executor disposes the claude session.</summary>
public sealed class RunCancellationRegistry
{
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _sources = new();

    public CancellationToken Register(long runId)
    {
        var cts = new CancellationTokenSource();
        _sources[runId] = cts;
        return cts.Token;
    }

    public bool Cancel(long runId)
    {
        if (_sources.TryGetValue(runId, out var cts))
        {
            cts.Cancel();
            return true;
        }

        return false;
    }

    public void Complete(long runId)
    {
        if (_sources.TryRemove(runId, out var cts))
        {
            cts.Dispose();
        }
    }

    public bool IsRunning(long runId) => _sources.ContainsKey(runId);
}

/// <summary>Wakes the run queue immediately when a run is enqueued.</summary>
public sealed class RunQueueSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
    });

    public void Wake() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }
}
