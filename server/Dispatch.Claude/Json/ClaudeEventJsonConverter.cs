using System.Text.Json;
using System.Text.Json.Serialization;
using Dispatch.Claude.Events;

namespace Dispatch.Claude.Json;

public sealed class ClaudeEventJsonConverter : JsonConverter<ClaudeEvent>
{
    public override ClaudeEvent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var raw = doc.RootElement.Clone();

        var type = TryGetString(raw, "type") ?? "";
        var subtype = TryGetString(raw, "subtype");

        return type switch
        {
            "system" when subtype == "init" => ParseSystemInit(raw, subtype),
            "assistant" => ParseAssistant(raw, subtype),
            "user" => ParseUser(raw, subtype),
            "result" => ParseResult(raw, subtype),
            _ => new UnknownEvent { RawType = type, Subtype = subtype, Raw = raw },
        };
    }

    public override void Write(Utf8JsonWriter writer, ClaudeEvent value, JsonSerializerOptions options)
    {
        throw new NotSupportedException(
            "ClaudeEvent is read-only. Serialize the original Raw JsonElement to ship events on the wire.");
    }

    private static SystemInitEvent ParseSystemInit(JsonElement raw, string? subtype)
    {
        var tools = new List<string>();
        if (raw.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in toolsEl.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String)
                {
                    var name = t.GetString();
                    if (name is not null)
                    {
                        tools.Add(name);
                    }
                }
            }
        }

        return new SystemInitEvent
        {
            RawType = "system",
            Subtype = subtype,
            Raw = raw,
            SessionId = TryGetString(raw, "session_id") ?? "",
            Model = TryGetString(raw, "model"),
            Tools = tools,
            Cwd = TryGetString(raw, "cwd"),
            PermissionMode = TryGetString(raw, "permissionMode") ?? TryGetString(raw, "permission_mode"),
        };
    }

    private static AssistantEvent ParseAssistant(JsonElement raw, string? subtype)
    {
        var msg = raw.TryGetProperty("message", out var m) ? m : default;

        var messageId = TryGetString(msg, "id") ?? "";
        var stopReason = TryGetString(msg, "stop_reason");

        var blocks = new List<AssistantContentBlock>();
        if (msg.ValueKind == JsonValueKind.Object
            && msg.TryGetProperty("content", out var contentEl)
            && contentEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in contentEl.EnumerateArray())
            {
                blocks.Add(ParseAssistantBlock(block));
            }
        }

        AssistantUsage? usage = null;
        if (msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
        {
            usage = new AssistantUsage
            {
                InputTokens = TryGetInt(usageEl, "input_tokens"),
                OutputTokens = TryGetInt(usageEl, "output_tokens"),
                CacheCreationInputTokens = TryGetInt(usageEl, "cache_creation_input_tokens"),
                CacheReadInputTokens = TryGetInt(usageEl, "cache_read_input_tokens"),
            };
        }

        return new AssistantEvent
        {
            RawType = "assistant",
            Subtype = subtype,
            Raw = raw,
            MessageId = messageId,
            Content = blocks,
            Usage = usage,
            StopReason = stopReason,
        };
    }

    private static AssistantContentBlock ParseAssistantBlock(JsonElement block)
    {
        var type = TryGetString(block, "type") ?? "";
        switch (type)
        {
            case "text":
                return new AssistantTextBlock { Type = type, Text = TryGetString(block, "text") ?? "" };
            case "thinking":
                return new AssistantThinkingBlock { Type = type, Thinking = TryGetString(block, "thinking") ?? "" };
            case "tool_use":
                var input = block.TryGetProperty("input", out var inputEl) ? inputEl.Clone() : default;
                return new AssistantToolUseBlock
                {
                    Type = type,
                    Id = TryGetString(block, "id") ?? "",
                    Name = TryGetString(block, "name") ?? "",
                    Input = input,
                };
            default:
                return new AssistantUnknownBlock { Type = type, Raw = block.Clone() };
        }
    }

    private static UserEvent ParseUser(JsonElement raw, string? subtype)
    {
        var msg = raw.TryGetProperty("message", out var m) ? m : default;

        var blocks = new List<UserContentBlock>();
        if (msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("content", out var contentEl))
        {
            if (contentEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in contentEl.EnumerateArray())
                {
                    blocks.Add(ParseUserBlock(block));
                }
            }
            else if (contentEl.ValueKind == JsonValueKind.String)
            {
                blocks.Add(new UserTextBlock { Type = "text", Text = contentEl.GetString() ?? "" });
            }
        }

        return new UserEvent
        {
            RawType = "user",
            Subtype = subtype,
            Raw = raw,
            Content = blocks,
        };
    }

    private static UserContentBlock ParseUserBlock(JsonElement block)
    {
        var type = TryGetString(block, "type") ?? "";
        switch (type)
        {
            case "text":
                return new UserTextBlock { Type = type, Text = TryGetString(block, "text") ?? "" };
            case "tool_result":
                var content = block.TryGetProperty("content", out var contentEl) ? contentEl.Clone() : default;
                return new UserToolResultBlock
                {
                    Type = type,
                    ToolUseId = TryGetString(block, "tool_use_id") ?? "",
                    ContentRaw = content,
                    IsError = TryGetBool(block, "is_error") ?? false,
                };
            default:
                return new UserUnknownBlock { Type = type, Raw = block.Clone() };
        }
    }

    private static ResultEvent ParseResult(JsonElement raw, string? subtype)
    {
        return new ResultEvent
        {
            RawType = "result",
            Subtype = subtype,
            Raw = raw,
            ResultSubtype = subtype ?? "",
            IsError = TryGetBool(raw, "is_error") ?? false,
            NumTurns = TryGetInt(raw, "num_turns"),
            DurationMs = TryGetDouble(raw, "duration_ms"),
            DurationApiMs = TryGetDouble(raw, "duration_api_ms"),
            FinalText = TryGetString(raw, "result"),
            SessionId = TryGetString(raw, "session_id"),
        };
    }

    private static string? TryGetString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!el.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static int? TryGetInt(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!el.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    }

    private static double? TryGetDouble(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!el.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
    }

    private static bool? TryGetBool(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!el.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}
