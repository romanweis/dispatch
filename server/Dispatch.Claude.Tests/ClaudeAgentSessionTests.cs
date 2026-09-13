using System.Diagnostics;
using Dispatch.Claude;
using Dispatch.Claude.Events;
using Xunit;

namespace Dispatch.Claude.Tests;

public sealed class ClaudeAgentSessionTests
{
    private static (string exe, IReadOnlyList<string> baseArgs) ResolveFakeClaude()
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, "FakeClaude", "fake-claude.dll");
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException(
                $"FakeClaude not found at expected path: {dllPath}. Did the build copy step run?");
        }

        var dotnet = "dotnet";
        var args = new List<string> { dllPath };
        return (dotnet, args);
    }

    private static ClaudeAgentSessionOptions BuildOptions(string mode)
    {
        var (exe, baseArgs) = ResolveFakeClaude();
        var args = baseArgs.ToList();
        args.Add("--mode");
        args.Add(mode);

        return new ClaudeAgentSessionOptions
        {
            WorkingDirectory = AppContext.BaseDirectory,
            ClaudeExecutable = exe,
            OverrideArguments = args,
            GracefulShutdown = TimeSpan.FromMilliseconds(500),
            EnvironmentOverrides = new Dictionary<string, string?> { ["FAKE_CLAUDE_SESSION_ID"] = "sess-test" },
        };
    }

    [Fact]
    public void BuildArgs_includes_stream_json_flags()
    {
        var args = ClaudeAgentSession.BuildArgs(new ClaudeAgentSessionOptions
        {
            WorkingDirectory = "C:/x",
        }).ToList();

        Assert.Contains("-p", args);
        Assert.Contains("--input-format", args);
        Assert.Contains("stream-json", args);
        Assert.Contains("--output-format", args);
        Assert.Contains("--verbose", args);
    }

    [Fact]
    public void BuildArgs_passes_resume_session_id()
    {
        var args = ClaudeAgentSession.BuildArgs(new ClaudeAgentSessionOptions
        {
            WorkingDirectory = "C:/x",
            ResumeSessionId = "sess-99",
        }).ToList();

        var idx = args.IndexOf("--resume");
        Assert.True(idx >= 0);
        Assert.Equal("sess-99", args[idx + 1]);
    }

    [Fact]
    public void BuildArgs_joins_allowed_tools_with_comma()
    {
        var args = ClaudeAgentSession.BuildArgs(new ClaudeAgentSessionOptions
        {
            WorkingDirectory = "C:/x",
            AllowedTools = ["Read", "Edit", "Bash"],
        }).ToList();

        var idx = args.IndexOf("--allowedTools");
        Assert.True(idx >= 0);
        Assert.Equal("Read,Edit,Bash", args[idx + 1]);
    }

    [Fact]
    public void BuildUserMessageLine_is_one_json_line_with_text_content()
    {
        var line = ClaudeAgentSession.BuildUserMessageLine("hello world");

        Assert.EndsWith("\n", line);
        var trimmed = line.TrimEnd('\n');
        Assert.DoesNotContain("\n", trimmed);

        using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
        Assert.Equal("user", doc.RootElement.GetProperty("type").GetString());
        var content = doc.RootElement.GetProperty("message").GetProperty("content");
        Assert.Equal(System.Text.Json.JsonValueKind.Array, content.ValueKind);
        var first = content[0];
        Assert.Equal("text", first.GetProperty("type").GetString());
        Assert.Equal("hello world", first.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Init_only_mode_emits_system_init_then_completes()
    {
        await using var session = new ClaudeAgentSession(BuildOptions("init-only"));
        await session.StartAsync(CancellationToken.None);

        var sessionIdTask = session.SessionStarted;
        var collected = new List<ClaudeEvent>();
        await foreach (var evt in session.Events.WithCancellation(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token))
        {
            collected.Add(evt);
        }

        Assert.Single(collected);
        var init = Assert.IsType<SystemInitEvent>(collected[0]);
        Assert.Equal("sess-test", init.SessionId);
        Assert.Equal("sess-test", await sessionIdTask);
        Assert.Equal("sess-test", session.CapturedSessionId);
    }

    [Fact]
    public async Task Echo_mode_round_trips_a_user_message()
    {
        await using var session = new ClaudeAgentSession(BuildOptions("echo"));
        await session.StartAsync(CancellationToken.None);

        var sessionId = await session.SessionStarted.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("sess-test", sessionId);

        await session.SendUserMessageAsync("ping", CancellationToken.None);

        var got = new List<ClaudeEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var evt in session.Events.WithCancellation(cts.Token))
        {
            got.Add(evt);
            if (evt is ResultEvent)
            {
                break;
            }
        }

        var assistant = Assert.Single(got.OfType<AssistantEvent>());
        var text = Assert.IsType<AssistantTextBlock>(Assert.Single(assistant.Content));
        Assert.Equal("echo: ping", text.Text);
        Assert.Single(got.OfType<ResultEvent>());
    }

    [Fact]
    public async Task Tool_use_mode_synthesises_a_tool_use_event()
    {
        await using var session = new ClaudeAgentSession(BuildOptions("tool-use"));
        await session.StartAsync(CancellationToken.None);

        await session.SessionStarted.WaitAsync(TimeSpan.FromSeconds(10));
        await session.SendUserMessageAsync("read the file", CancellationToken.None);

        var got = new List<ClaudeEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var evt in session.Events.WithCancellation(cts.Token))
        {
            got.Add(evt);
            if (evt is ResultEvent)
            {
                break;
            }
        }

        Assert.Single(got.OfType<AssistantEvent>());
        var toolUse = Assert.Single(got.OfType<ToolUseEvent>());
        Assert.Equal("Read", toolUse.ToolName);
        Assert.Equal("toolu_1", toolUse.ToolUseId);
    }

    [Fact]
    public async Task Disposing_a_long_running_session_kills_the_process_within_grace()
    {
        var sw = Stopwatch.StartNew();
        ClaudeAgentSession session;
        Task<int> exitTask;
        await using ((session = new ClaudeAgentSession(BuildOptions("delay"))).ConfigureAwait(false))
        {
            await session.StartAsync(CancellationToken.None);
            await session.SessionStarted.WaitAsync(TimeSpan.FromSeconds(10));
            exitTask = session.ProcessExited;
        }

        sw.Stop();
        var exitCode = await exitTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.InRange(sw.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        _ = exitCode;
    }
}

public sealed class ResolveLauncherTests
{
    [Xunit.Fact]
    public void WithoutPrefix_UsesClaudeExecutable()
    {
        var (file, leading) = Dispatch.Claude.ClaudeAgentSession.ResolveLauncher(new Dispatch.Claude.ClaudeAgentSessionOptions { WorkingDirectory = "." });
        Xunit.Assert.Equal("claude", file);
        Xunit.Assert.Empty(leading);
    }

    [Xunit.Fact]
    public void WithPrefix_UsesPrefixAsLauncherAndAppendsExecutable()
    {
        var opts = new Dispatch.Claude.ClaudeAgentSessionOptions
        {
            WorkingDirectory = ".",
            ExecutablePrefix = new[] { "incus", "exec", "t-1", "--" },
        };
        var (file, leading) = Dispatch.Claude.ClaudeAgentSession.ResolveLauncher(opts);
        Xunit.Assert.Equal("incus", file);
        Xunit.Assert.Equal(new[] { "exec", "t-1", "--", "claude" }, leading);
    }
}
