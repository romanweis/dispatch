using Dispatch.Api.Config;
using Dispatch.Api.Data;

namespace Dispatch.Api.Incus;

public enum ContainerState
{
    None,
    Running,
    Stopped,
    Missing,
}

public sealed record ExecResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Success => ExitCode == 0;
}

public interface IIncusService
{
    /// <summary>Creates (copy from base, configure env, start, wait for network, push CLI) or starts the ticket container.</summary>
    Task<ContainerState> EnsureTicketContainerAsync(Ticket ticket, LoadedProject project, CancellationToken ct = default);

    Task<ContainerState> GetStateAsync(string name, CancellationToken ct = default);

    /// <summary>Returns the file content or null when the file (or container) does not exist.</summary>
    Task<string?> PullFileAsync(string name, string path, CancellationToken ct = default);

    Task PushFileAsync(string name, string path, string content, int uid = 1000, int gid = 1000, string mode = "0644", CancellationToken ct = default);

    Task<ExecResult> ExecAsync(string name, IReadOnlyList<string> args, string? cwd, bool asAgent = true, CancellationToken ct = default);

    Task DeleteAsync(string name, bool snapshot, CancellationToken ct = default);

    /// <summary>The ExecutablePrefix for ClaudeAgentSessionOptions: `incus exec t-N --user 1000 ... --`.</summary>
    IReadOnlyList<string> BuildClaudePrefix(string name, string cwd, IReadOnlyDictionary<string, string> env);

    /// <summary>Environment variables every ticket container receives (DISPATCH_URL, DISPATCH_TOKEN, TICKET_ID, DISPATCH_PROJECT + project env).</summary>
    IReadOnlyDictionary<string, string> BuildTicketEnvironment(Ticket ticket, LoadedProject project);
}
