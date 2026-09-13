using System.Text.Json;

namespace Dispatch.Claude.Events;

public sealed record UserEvent : ClaudeEvent
{
    public required IReadOnlyList<UserContentBlock> Content { get; init; }
}

public abstract record UserContentBlock
{
    public required string Type { get; init; }
}

public sealed record UserTextBlock : UserContentBlock
{
    public required string Text { get; init; }
}

public sealed record UserToolResultBlock : UserContentBlock
{
    public required string ToolUseId { get; init; }
    public required JsonElement ContentRaw { get; init; }
    public bool IsError { get; init; }
}

public sealed record UserUnknownBlock : UserContentBlock
{
    public required JsonElement Raw { get; init; }
}
