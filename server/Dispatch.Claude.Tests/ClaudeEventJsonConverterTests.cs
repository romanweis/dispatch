using Dispatch.Claude.Events;
using Dispatch.Claude.Json;
using Xunit;

namespace Dispatch.Claude.Tests;

public sealed class ClaudeEventJsonConverterTests
{
    [Fact]
    public void Parses_system_init_event()
    {
        const string line = "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"sess-1\",\"model\":\"claude-opus-4-7\",\"cwd\":\"C:/x\",\"tools\":[\"Read\",\"Edit\"],\"permissionMode\":\"default\"}";

        var evt = ClaudeEventParser.Parse(line);

        var init = Assert.IsType<SystemInitEvent>(evt);
        Assert.Equal("system", init.RawType);
        Assert.Equal("init", init.Subtype);
        Assert.Equal("sess-1", init.SessionId);
        Assert.Equal("claude-opus-4-7", init.Model);
        Assert.Equal("C:/x", init.Cwd);
        Assert.Equal(["Read", "Edit"], init.Tools);
        Assert.Equal("default", init.PermissionMode);
    }

    [Fact]
    public void Parses_assistant_text_event()
    {
        const string line = "{\"type\":\"assistant\",\"message\":{\"id\":\"msg_1\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":4,\"output_tokens\":2}}}";

        var evt = ClaudeEventParser.Parse(line);

        var assistant = Assert.IsType<AssistantEvent>(evt);
        Assert.Equal("msg_1", assistant.MessageId);
        Assert.Equal("end_turn", assistant.StopReason);
        Assert.NotNull(assistant.Usage);
        Assert.Equal(4, assistant.Usage!.InputTokens);
        Assert.Equal(2, assistant.Usage.OutputTokens);
        var block = Assert.IsType<AssistantTextBlock>(Assert.Single(assistant.Content));
        Assert.Equal("hi", block.Text);
    }

    [Fact]
    public void Parses_assistant_with_tool_use_block()
    {
        const string line = "{\"type\":\"assistant\",\"message\":{\"id\":\"msg_2\",\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"Read\",\"input\":{\"file_path\":\"a.txt\"}}],\"stop_reason\":\"tool_use\"}}";

        var evt = ClaudeEventParser.Parse(line);

        var assistant = Assert.IsType<AssistantEvent>(evt);
        var toolBlock = Assert.IsType<AssistantToolUseBlock>(Assert.Single(assistant.Content));
        Assert.Equal("toolu_1", toolBlock.Id);
        Assert.Equal("Read", toolBlock.Name);
        Assert.Equal("a.txt", toolBlock.Input.GetProperty("file_path").GetString());
    }

    [Fact]
    public void Expand_synthesises_tool_use_event_after_assistant()
    {
        const string line = "{\"type\":\"assistant\",\"message\":{\"id\":\"msg_3\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"calling\"},{\"type\":\"tool_use\",\"id\":\"toolu_9\",\"name\":\"Bash\",\"input\":{\"command\":\"ls\"}}],\"stop_reason\":\"tool_use\"}}";

        var evt = ClaudeEventParser.Parse(line);
        Assert.NotNull(evt);
        var expanded = ClaudeEventParser.Expand(evt!).ToList();

        Assert.Equal(2, expanded.Count);
        Assert.IsType<AssistantEvent>(expanded[0]);
        var toolUse = Assert.IsType<ToolUseEvent>(expanded[1]);
        Assert.Equal("toolu_9", toolUse.ToolUseId);
        Assert.Equal("Bash", toolUse.ToolName);
        Assert.Equal("msg_3", toolUse.ParentMessageId);
        Assert.Equal("ls", toolUse.Input.GetProperty("command").GetString());
    }

    [Fact]
    public void Parses_user_tool_result_event()
    {
        const string line = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"toolu_5\",\"content\":\"ok\",\"is_error\":false}]}}";

        var evt = ClaudeEventParser.Parse(line);

        var user = Assert.IsType<UserEvent>(evt);
        var block = Assert.IsType<UserToolResultBlock>(Assert.Single(user.Content));
        Assert.Equal("toolu_5", block.ToolUseId);
        Assert.False(block.IsError);
    }

    [Fact]
    public void Parses_result_event()
    {
        const string line = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"num_turns\":3,\"duration_ms\":12.5,\"duration_api_ms\":10.0,\"session_id\":\"sess-9\",\"result\":\"final text\"}";

        var evt = ClaudeEventParser.Parse(line);

        var result = Assert.IsType<ResultEvent>(evt);
        Assert.Equal("success", result.ResultSubtype);
        Assert.False(result.IsError);
        Assert.Equal(3, result.NumTurns);
        Assert.Equal(12.5, result.DurationMs);
        Assert.Equal("final text", result.FinalText);
        Assert.Equal("sess-9", result.SessionId);
    }

    [Fact]
    public void Unknown_type_falls_through_to_unknown_event()
    {
        const string line = "{\"type\":\"future_kind\",\"data\":42}";

        var evt = ClaudeEventParser.Parse(line);

        var unknown = Assert.IsType<UnknownEvent>(evt);
        Assert.Equal("future_kind", unknown.RawType);
    }

    [Fact]
    public void Empty_line_returns_null()
    {
        Assert.Null(ClaudeEventParser.Parse(""));
        Assert.Null(ClaudeEventParser.Parse("   "));
    }

    [Fact]
    public async Task Golden_basic_session_fixture_parses_in_order()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "session-basic.jsonl");
        var lines = await File.ReadAllLinesAsync(path);

        var events = lines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(ClaudeEventParser.Parse)
            .Where(e => e is not null)
            .Cast<ClaudeEvent>()
            .ToList();

        Assert.Equal(3, events.Count);
        Assert.IsType<SystemInitEvent>(events[0]);
        Assert.IsType<AssistantEvent>(events[1]);
        Assert.IsType<ResultEvent>(events[2]);
        Assert.Equal("sess-abc123", ((SystemInitEvent)events[0]).SessionId);
    }

    [Fact]
    public async Task Golden_tool_session_fixture_synthesises_tool_use_event()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "session-with-tool.jsonl");
        var lines = await File.ReadAllLinesAsync(path);

        var expanded = lines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(ClaudeEventParser.Parse)
            .Where(e => e is not null)
            .SelectMany(e => ClaudeEventParser.Expand(e!))
            .ToList();

        Assert.Single(expanded.OfType<ToolUseEvent>());
        var toolUse = expanded.OfType<ToolUseEvent>().Single();
        Assert.Equal("Read", toolUse.ToolName);
        Assert.Equal("msg_a", toolUse.ParentMessageId);

        Assert.Single(expanded.OfType<UserEvent>());
        var user = expanded.OfType<UserEvent>().Single();
        var resultBlock = Assert.IsType<UserToolResultBlock>(Assert.Single(user.Content));
        Assert.Equal("toolu_42", resultBlock.ToolUseId);
    }
}
