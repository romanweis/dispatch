using System.Text.Json;

namespace Dispatch.Claude.Events;

public sealed record AssistantEvent : ClaudeEvent
{
    public required string MessageId { get; init; }
    public required IReadOnlyList<AssistantContentBlock> Content { get; init; }
    public AssistantUsage? Usage { get; init; }
    public string? StopReason { get; init; }
}

public abstract record AssistantContentBlock
{
    public required string Type { get; init; }
}

public sealed record AssistantTextBlock : AssistantContentBlock
{
    public required string Text { get; init; }
}

public sealed record AssistantThinkingBlock : AssistantContentBlock
{
    public required string Thinking { get; init; }
}

public sealed record AssistantToolUseBlock : AssistantContentBlock
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required JsonElement Input { get; init; }
}

public sealed record AssistantUnknownBlock : AssistantContentBlock
{
    public required JsonElement Raw { get; init; }
}

public sealed record AssistantUsage
{
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? CacheCreationInputTokens { get; init; }
    public int? CacheReadInputTokens { get; init; }
}
