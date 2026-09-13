using System.Text.Json;
using Dispatch.Claude.Events;

namespace Dispatch.Claude.Json;

public static class ClaudeEventParser
{
    public static JsonSerializerOptions Options { get; } = BuildOptions();

    public static ClaudeEvent? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        return JsonSerializer.Deserialize<ClaudeEvent>(line, Options);
    }

    public static IEnumerable<ClaudeEvent> Expand(ClaudeEvent evt)
    {
        yield return evt;

        if (evt is AssistantEvent assistant)
        {
            foreach (var block in assistant.Content)
            {
                if (block is AssistantToolUseBlock toolUse)
                {
                    yield return new ToolUseEvent
                    {
                        RawType = evt.RawType,
                        Subtype = evt.Subtype,
                        Raw = evt.Raw,
                        ToolUseId = toolUse.Id,
                        ToolName = toolUse.Name,
                        Input = toolUse.Input,
                        ParentMessageId = assistant.MessageId,
                    };
                }
            }
        }
    }

    private static JsonSerializerOptions BuildOptions()
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };
        opts.Converters.Add(new ClaudeEventJsonConverter());
        return opts;
    }
}
