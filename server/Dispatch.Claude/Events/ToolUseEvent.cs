using System.Text.Json;

namespace Dispatch.Claude.Events;

public sealed record ToolUseEvent : ClaudeEvent
{
    public required string ToolUseId { get; init; }
    public required string ToolName { get; init; }
    public required JsonElement Input { get; init; }
    public required string ParentMessageId { get; init; }
}
