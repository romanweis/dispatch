namespace Dispatch.Claude.Events;

public sealed record SystemInitEvent : ClaudeEvent
{
    public required string SessionId { get; init; }
    public string? Model { get; init; }
    public IReadOnlyList<string> Tools { get; init; } = [];
    public string? Cwd { get; init; }
    public string? PermissionMode { get; init; }
}
