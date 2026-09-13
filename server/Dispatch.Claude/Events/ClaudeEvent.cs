using System.Text.Json;
using System.Text.Json.Serialization;
using Dispatch.Claude.Json;

namespace Dispatch.Claude.Events;

[JsonConverter(typeof(ClaudeEventJsonConverter))]
public abstract record ClaudeEvent
{
    public required string RawType { get; init; }
    public string? Subtype { get; init; }
    public required JsonElement Raw { get; init; }
}
