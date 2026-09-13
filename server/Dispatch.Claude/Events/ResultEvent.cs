namespace Dispatch.Claude.Events;

public sealed record ResultEvent : ClaudeEvent
{
    public required string ResultSubtype { get; init; }
    public required bool IsError { get; init; }
    public int? NumTurns { get; init; }
    public double? DurationMs { get; init; }
    public double? DurationApiMs { get; init; }
    public string? FinalText { get; init; }
    public string? SessionId { get; init; }
}
