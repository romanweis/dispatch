using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dispatch.Claude.Events;
using Dispatch.Claude.Json;
using Dispatch.Claude.Process;
using SysProcess = System.Diagnostics.Process;

namespace Dispatch.Claude;

public sealed class ClaudeAgentSession : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ClaudeAgentSessionOptions _options;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Channel<ClaudeEvent> _events = Channel.CreateUnbounded<ClaudeEvent>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
    private readonly TaskCompletionSource<string> _sessionStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _sessionCts = new();

    private SysProcess? _process;
    private Task? _stdoutPump;
    private Task? _stderrPump;
    private int _disposed;

    public ClaudeAgentSession(ClaudeAgentSessionOptions options)
    {
        _options = options;
    }

    public string? CapturedSessionId { get; private set; }

    public Task<string> SessionStarted => _sessionStarted.Task;

    public Action<string>? OnStderrLine { get; set; }

    public Task<int> ProcessExited => _processExitedTcs.Task;

    private readonly TaskCompletionSource<int> _processExitedTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IAsyncEnumerable<ClaudeEvent> Events => ReadEventsAsync();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("Session already started.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var (fileName, leadingArgs) = ResolveLauncher(_options);
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = _options.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };

        foreach (var arg in leadingArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var arg in BuildArgs(_options))
        {
            psi.ArgumentList.Add(arg);
        }

        if (_options.EnvironmentOverrides is { } env)
        {
            foreach (var kv in env)
            {
                psi.Environment[kv.Key] = kv.Value;
            }
        }

        var proc = new SysProcess
        {
            StartInfo = psi,
            EnableRaisingEvents = true,
        };
        proc.Exited += (_, _) =>
        {
            _processExitedTcs.TrySetResult(proc.ExitCode);
        };

        if (!proc.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {_options.ClaudeExecutable}");
        }

        _process = proc;
        _stdoutPump = Task.Run(() => PumpStdoutAsync(proc, _sessionCts.Token));
        _stderrPump = Task.Run(() => PumpStderrAsync(proc, _sessionCts.Token));

        if (!string.IsNullOrEmpty(_options.InitialPrompt))
        {
            return WriteInitialPromptAsync(_options.InitialPrompt!, cancellationToken);
        }

        return Task.CompletedTask;
    }

    private async Task WriteInitialPromptAsync(string text, CancellationToken cancellationToken)
    {
        var payload = BuildUserMessageLine(text);
        var stdin = _process!.StandardInput;
        await stdin.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
        await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static (string FileName, IReadOnlyList<string> LeadingArgs) ResolveLauncher(ClaudeAgentSessionOptions options)
    {
        if (options.ExecutablePrefix is { Count: > 0 } prefix)
        {
            var leading = new List<string>(prefix.Count);
            leading.AddRange(prefix.Skip(1));
            leading.Add(options.ClaudeExecutable);
            return (prefix[0], leading);
        }

        return (options.ClaudeExecutable, Array.Empty<string>());
    }

    internal static IEnumerable<string> BuildArgs(ClaudeAgentSessionOptions options)
    {
        if (options.OverrideArguments is { } overrideArgs)
        {
            foreach (var a in overrideArgs)
            {
                yield return a;
            }

            yield break;
        }

        yield return "-p";
        yield return "--input-format";
        yield return "stream-json";
        yield return "--output-format";
        yield return "stream-json";
        yield return "--verbose";

        if (!string.IsNullOrWhiteSpace(options.ResumeSessionId))
        {
            yield return "--resume";
            yield return options.ResumeSessionId;
        }

        if (options.AllowedTools is { Count: > 0 } tools)
        {
            yield return "--allowedTools";
            yield return string.Join(",", tools);
        }

        if (!string.IsNullOrWhiteSpace(options.PermissionMode))
        {
            yield return "--permission-mode";
            yield return options.PermissionMode;
        }

        if (options.AdditionalArgs is { } extras)
        {
            foreach (var a in extras)
            {
                yield return a;
            }
        }
    }

    public async ValueTask SendUserMessageAsync(string text, CancellationToken cancellationToken)
    {
        if (_process is null)
        {
            throw new InvalidOperationException("Session has not been started.");
        }

        if (_process.HasExited)
        {
            throw new InvalidOperationException("Underlying claude process has exited.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sessionCts.Token);

        var payload = BuildUserMessageLine(text);

        await _writeLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var stdin = _process.StandardInput;
            await stdin.WriteAsync(payload.AsMemory(), linked.Token).ConfigureAwait(false);
            await stdin.FlushAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal static string BuildUserMessageLine(string text)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("type", "user");
            w.WriteStartObject("message");
            w.WriteString("role", "user");
            w.WriteStartArray("content");
            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", text);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return Utf8NoBom.GetString(ms.ToArray()) + "\n";
    }

    private async IAsyncEnumerable<ClaudeEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var reader = _events.Reader;
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var evt))
            {
                yield return evt;
            }
        }
    }

    private async Task PumpStdoutAsync(SysProcess proc, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await proc.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                ClaudeEvent? parsed;
                try
                {
                    parsed = ClaudeEventParser.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (parsed is null)
                {
                    continue;
                }

                if (CapturedSessionId is null && TryReadTopLevelSessionId(parsed.Raw, out var earlySid))
                {
                    CapturedSessionId = earlySid;
                    _sessionStarted.TrySetResult(earlySid);
                }

                foreach (var emit in ClaudeEventParser.Expand(parsed))
                {
                    await _events.Writer.WriteAsync(emit, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _events.Writer.TryComplete(ex);
            _sessionStarted.TrySetException(ex);
            return;
        }

        _events.Writer.TryComplete();
        if (!_sessionStarted.Task.IsCompleted)
        {
            _sessionStarted.TrySetCanceled();
        }
    }

    private static bool TryReadTopLevelSessionId(JsonElement raw, out string sessionId)
    {
        sessionId = "";
        if (raw.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!raw.TryGetProperty("session_id", out var sid) || sid.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = sid.GetString();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        sessionId = value;
        return true;
    }

    private async Task PumpStderrAsync(SysProcess proc, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await proc.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                OnStderrLine?.Invoke(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _sessionCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        var proc = _process;
        if (proc is not null)
        {
            await ProcessTreeKiller.ShutdownAsync(proc, _options.GracefulShutdown, CancellationToken.None)
                .ConfigureAwait(false);
        }

        if (_stdoutPump is not null)
        {
            try
            {
                await _stdoutPump.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        if (_stderrPump is not null)
        {
            try
            {
                await _stderrPump.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _events.Writer.TryComplete();

        if (proc is not null)
        {
            _processExitedTcs.TrySetResult(proc.HasExited ? proc.ExitCode : -1);
            proc.Dispose();
        }

        _writeLock.Dispose();
        _sessionCts.Dispose();
    }
}
