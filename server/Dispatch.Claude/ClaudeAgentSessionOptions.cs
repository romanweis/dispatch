namespace Dispatch.Claude;

public sealed record ClaudeAgentSessionOptions
{
    public required string WorkingDirectory { get; init; }

    public string ClaudeExecutable { get; init; } = "claude";

    /// <summary>
    /// Optional launcher placed in front of <see cref="ClaudeExecutable"/>, e.g.
    /// ["incus","exec","t-12","--user","1000","--group","1000","--cwd","/home/agent/ws","--env","HOME=/home/agent","--"].
    /// When set, the first element is the process to start and the rest are prepended to the argument list.
    /// </summary>
    public IReadOnlyList<string>? ExecutablePrefix { get; init; }

    public string? ResumeSessionId { get; init; }

    public IReadOnlyList<string>? AllowedTools { get; init; }

    public string? PermissionMode { get; init; }

    public IReadOnlyList<string>? AdditionalArgs { get; init; }

    /// <summary>
    /// Initial user message written to claude's stdin immediately after spawn.
    /// Required by claude -p --input-format stream-json — without an initial
    /// prompt or stdin content, claude exits with "Input must be provided".
    /// </summary>
    public string? InitialPrompt { get; init; }

    /// <summary>
    /// When set, used verbatim instead of building the standard
    /// `-p --input-format stream-json --output-format stream-json --verbose ...` command line.
    /// Intended for tests that wrap a different binary; production callers should leave this null.
    /// </summary>
    public IReadOnlyList<string>? OverrideArguments { get; init; }

    public IReadOnlyDictionary<string, string?>? EnvironmentOverrides { get; init; }

    public TimeSpan GracefulShutdown { get; init; } = TimeSpan.FromSeconds(2);
}
