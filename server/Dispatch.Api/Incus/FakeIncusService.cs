using System.Collections.Concurrent;
using Dispatch.Api.Config;
using Dispatch.Api.Data;

namespace Dispatch.Api.Incus;

/// <summary>In-memory incus for local development and tests. Records every call in <see cref="Calls"/>.</summary>
public sealed class FakeIncusService(ILogger<FakeIncusService> logger) : IIncusService
{
    private readonly ConcurrentDictionary<string, ContainerState> _states = new();
    private readonly ConcurrentDictionary<string, string> _files = new();

    public ConcurrentQueue<string> Calls { get; } = new();

    public string PublicUrl { get; set; } = "http://127.0.0.1:9300";

    /// <summary>Optional hook: returns fake exec results per command (defaults to exit 0).</summary>
    public Func<string, IReadOnlyList<string>, ExecResult>? ExecHandler { get; set; }

    public IReadOnlyDictionary<string, ContainerState> States => _states;

    public void SetFile(string name, string path, string content) => _files[$"{name}{path}"] = content;

    public Task<ContainerState> EnsureTicketContainerAsync(Ticket ticket, LoadedProject project, CancellationToken ct = default)
    {
        var name = ticket.ContainerName;
        Calls.Enqueue($"ensure {name}");
        _states[name] = ContainerState.Running;
        logger.LogInformation("[fake incus] ensured container {Container}", name);
        return Task.FromResult(ContainerState.Running);
    }

    public Task<ContainerState> GetStateAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(_states.GetValueOrDefault(name, ContainerState.Missing));

    public Task<string?> PullFileAsync(string name, string path, CancellationToken ct = default)
    {
        Calls.Enqueue($"pull {name}{path}");
        return Task.FromResult(_files.GetValueOrDefault($"{name}{path}"));
    }

    public Task PushFileAsync(string name, string path, string content, int uid = 1000, int gid = 1000, string mode = "0644", CancellationToken ct = default)
    {
        Calls.Enqueue($"push {name}{path}");
        _files[$"{name}{path}"] = content;
        return Task.CompletedTask;
    }

    public Task<ExecResult> ExecAsync(string name, IReadOnlyList<string> args, string? cwd, bool asAgent = true, CancellationToken ct = default)
    {
        Calls.Enqueue($"exec {name} {string.Join(' ', args)}");
        var result = ExecHandler?.Invoke(name, args) ?? new ExecResult(0, "", "");
        return Task.FromResult(result);
    }

    public Task DeleteAsync(string name, bool snapshot, CancellationToken ct = default)
    {
        Calls.Enqueue(snapshot ? $"snapshot+delete {name}" : $"delete {name}");
        _states[name] = ContainerState.Missing;
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> BuildClaudePrefix(string name, string cwd, IReadOnlyDictionary<string, string> env) => [];

    public IReadOnlyDictionary<string, string> BuildTicketEnvironment(Ticket ticket, LoadedProject project) =>
        new Dictionary<string, string>
        {
            ["DISPATCH_URL"] = PublicUrl,
            ["DISPATCH_TOKEN"] = ticket.Token,
            ["TICKET_ID"] = ticket.Id.ToString(),
            ["DISPATCH_PROJECT"] = project.Config.Name,
        };
}
