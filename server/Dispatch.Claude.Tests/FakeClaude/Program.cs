using System.Globalization;
using System.Text;
using System.Text.Json;

var mode = "echo";
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--mode")
    {
        mode = args[i + 1];
    }
}

Console.OutputEncoding = new UTF8Encoding(false);
using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
{
    AutoFlush = true,
    NewLine = "\n",
};
using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

var sessionId = Environment.GetEnvironmentVariable("FAKE_CLAUDE_SESSION_ID")
    ?? "fake-" + Guid.NewGuid().ToString("N")[..8];

void EmitInit()
{
    var init = new
    {
        type = "system",
        subtype = "init",
        session_id = sessionId,
        model = "fake-model",
        cwd = Directory.GetCurrentDirectory(),
        tools = new[] { "Read", "Write", "Bash" },
        permissionMode = "default",
    };
    stdout.WriteLine(JsonSerializer.Serialize(init));
}

void EmitAssistantText(string text, string messageId)
{
    var msg = new
    {
        type = "assistant",
        message = new
        {
            id = messageId,
            role = "assistant",
            content = new[]
            {
                new { type = "text", text },
            },
            stop_reason = "end_turn",
            usage = new { input_tokens = 1, output_tokens = text.Length },
        },
    };
    stdout.WriteLine(JsonSerializer.Serialize(msg));
}

void EmitAssistantToolUse(string toolName, object input, string toolUseId, string messageId)
{
    var msg = new
    {
        type = "assistant",
        message = new
        {
            id = messageId,
            role = "assistant",
            content = new object[]
            {
                new { type = "tool_use", id = toolUseId, name = toolName, input },
            },
            stop_reason = "tool_use",
        },
    };
    stdout.WriteLine(JsonSerializer.Serialize(msg));
}

void EmitResult(string subtype, double durationMs)
{
    var msg = new
    {
        type = "result",
        subtype,
        is_error = subtype != "success",
        num_turns = 1,
        duration_ms = durationMs,
        duration_api_ms = durationMs,
        session_id = sessionId,
        result = (string?)null,
    };
    stdout.WriteLine(JsonSerializer.Serialize(msg));
}

switch (mode)
{
    case "init-only":
        EmitInit();
        return 0;

    case "delay":
        EmitInit();
        await Task.Delay(Timeout.Infinite);
        return 0;

    case "tool-use":
        EmitInit();
        var tline = await stdin.ReadLineAsync();
        _ = tline;
        EmitAssistantToolUse("Read", new { file_path = "/tmp/x" }, "toolu_1", "msg_1");
        EmitResult("success", 12.0);
        return 0;

    case "echo":
    default:
        EmitInit();
        string? line;
        var counter = 0;
        while ((line = await stdin.ReadLineAsync()) is not null)
        {
            counter++;
            var text = ExtractUserText(line) ?? "(no text)";
            EmitAssistantText("echo: " + text, "msg_" + counter.ToString(CultureInfo.InvariantCulture));
            EmitResult("success", 5.0);
        }
        return 0;
}

static string? ExtractUserText(string jsonLine)
{
    try
    {
        using var doc = JsonDocument.Parse(jsonLine);
        if (!doc.RootElement.TryGetProperty("message", out var msg))
        {
            return null;
        }

        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                && block.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
            {
                return txt.GetString();
            }
        }
    }
    catch (JsonException)
    {
    }

    return null;
}
