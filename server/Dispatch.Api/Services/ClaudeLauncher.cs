using System.Text.Json;
using System.Threading.Channels;
using Dispatch.Claude;
using Dispatch.Claude.Events;
using Dispatch.Claude.Json;

namespace Dispatch.Api.Services;

/// <summary>A started claude process (real or simulated) as seen by the run queue.</summary>
public interface IClaudeRun : IAsyncDisposable
{
    IAsyncEnumerable<ClaudeEvent> Events { get; }

    Task<int> ProcessExited { get; }

    string? CapturedSessionId { get; }
}

public interface IClaudeLauncher
{
    Task<IClaudeRun> LaunchAsync(ClaudeAgentSessionOptions options, Action<string>? onStderr, CancellationToken ct);
}

/// <summary>Production launcher: wraps <see cref="ClaudeAgentSession"/>.</summary>
public sealed class ClaudeSessionLauncher : IClaudeLauncher
{
    public async Task<IClaudeRun> LaunchAsync(ClaudeAgentSessionOptions options, Action<string>? onStderr, CancellationToken ct)
    {
        var session = new ClaudeAgentSession(options) { OnStderrLine = onStderr };
        try
        {
            await session.StartAsync(ct);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }

        return new SessionRun(session);
    }

    private sealed class SessionRun(ClaudeAgentSession session) : IClaudeRun
    {
        public IAsyncEnumerable<ClaudeEvent> Events => session.Events;

        public Task<int> ProcessExited => session.ProcessExited;

        public string? CapturedSessionId => session.CapturedSessionId;

        public ValueTask DisposeAsync() => session.DisposeAsync();
    }
}

/// <summary>
/// FakeIncus launcher. With <see cref="DispatchOptions.FakeClaude"/> set it runs the FakeClaude test binary locally;
/// otherwise it emits a synthetic init / assistant / result sequence.
/// </summary>
public sealed class FakeClaudeLauncher(string? fakeClaudePath, ILogger<FakeClaudeLauncher> logger) : IClaudeLauncher
{
    private readonly ClaudeSessionLauncher _real = new();

    public Task<IClaudeRun> LaunchAsync(ClaudeAgentSessionOptions options, Action<string>? onStderr, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(fakeClaudePath) && File.Exists(fakeClaudePath))
        {
            var isDll = fakeClaudePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            var patched = options with
            {
                WorkingDirectory = Directory.GetCurrentDirectory(),
                ExecutablePrefix = isDll ? ["dotnet"] : null,
                ClaudeExecutable = fakeClaudePath,
                ResumeSessionId = null,
                EnvironmentOverrides = options.ResumeSessionId is null
                    ? options.EnvironmentOverrides
                    : new Dictionary<string, string?>(options.EnvironmentOverrides ?? new Dictionary<string, string?>())
                    {
                        ["FAKE_CLAUDE_SESSION_ID"] = options.ResumeSessionId,
                    },
            };
            logger.LogInformation("[fake claude] launching {Path}", fakeClaudePath);
            return _real.LaunchAsync(patched, onStderr, ct);
        }

        logger.LogInformation("[fake claude] simulating a run (session {Session})", options.ResumeSessionId ?? "new");
        return Task.FromResult<IClaudeRun>(new SimulatedRun(options.ResumeSessionId, options.InitialPrompt ?? ""));
    }

    private sealed class SimulatedRun : IClaudeRun
    {
        private readonly Channel<ClaudeEvent> _events = Channel.CreateUnbounded<ClaudeEvent>();
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cts = new();
        private readonly string _sessionId;
        private int _disposed;

        public SimulatedRun(string? resume, string prompt)
        {
            _sessionId = resume ?? "fake-" + Guid.NewGuid().ToString("N")[..12];
            _ = Task.Run(() => ProduceAsync(prompt));
        }

        public IAsyncEnumerable<ClaudeEvent> Events => _events.Reader.ReadAllAsync();

        public Task<int> ProcessExited => _exited.Task;

        public string? CapturedSessionId { get; private set; }

        private async Task ProduceAsync(string prompt)
        {
            try
            {
                Emit(new { type = "system", subtype = "init", session_id = _sessionId, model = "fake-model", cwd = "/fake", tools = new[] { "Bash" }, permissionMode = "bypassPermissions" });
                CapturedSessionId = _sessionId;
                await Task.Delay(300, _cts.Token);
                Emit(new
                {
                    type = "assistant",
                    session_id = _sessionId,
                    message = new
                    {
                        id = "msg_fake",
                        role = "assistant",
                        content = new[] { new { type = "text", text = "fake run (" + prompt.Length + " chars of prompt)" } },
                        stop_reason = "end_turn",
                    },
                });
                await Task.Delay(300, _cts.Token);
                Emit(new { type = "result", subtype = "success", is_error = false, num_turns = 1, duration_ms = 600, session_id = _sessionId, result = "fake run" });
                _events.Writer.TryComplete();
                _exited.TrySetResult(0);
            }
            catch (OperationCanceledException)
            {
                _events.Writer.TryComplete();
                _exited.TrySetResult(-1);
            }
        }

        private void Emit(object payload)
        {
            var line = JsonSerializer.Serialize(payload);
            var evt = ClaudeEventParser.Parse(line);
            if (evt is not null)
            {
                _events.Writer.TryWrite(evt);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            _cts.Cancel();
            _events.Writer.TryComplete();
            _exited.TrySetResult(-1);
            _cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
