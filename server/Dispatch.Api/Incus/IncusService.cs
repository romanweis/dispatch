using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dispatch.Api.Config;
using Dispatch.Api.Data;
using Microsoft.Extensions.Options;

namespace Dispatch.Api.Incus;

/// <summary>Drives the `incus` CLI. Every invocation uses ProcessStartInfo.ArgumentList (never a shell string).</summary>
public sealed class IncusService(
    IOptions<DispatchOptions> options,
    PublicUrlResolver publicUrl,
    ILogger<IncusService> logger) : IIncusService
{
    private static readonly TimeSpan StateCacheTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NetworkWait = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, (ContainerState State, DateTime At)> _stateCache = new();

    public async Task<ContainerState> EnsureTicketContainerAsync(Ticket ticket, LoadedProject project, CancellationToken ct = default)
    {
        var name = ticket.ContainerName;
        var state = await QueryStateAsync(name, ct);

        if (state == ContainerState.Missing)
        {
            logger.LogInformation("Creating container {Container} from {Base}", name, project.Config.BaseContainer);
            await RunOrThrowAsync(["copy", project.Config.BaseContainer, name], ct);
            if (!string.IsNullOrWhiteSpace(options.Value.IncusProfile))
            {
                // Non-fatal: the base may already carry the profile.
                await RunAsync(["profile", "add", name, options.Value.IncusProfile], ct, logFailureAsWarning: false);
            }

            var configArgs = new List<string> { "config", "set", name };
            foreach (var (key, value) in BuildTicketEnvironment(ticket, project))
            {
                configArgs.Add($"environment.{key}={value}");
            }

            await RunOrThrowAsync(configArgs, ct);
            await RunOrThrowAsync(["start", name], ct);
            await WaitForNetworkAsync(name, ct);
            await PushHostFileAsync(name, options.Value.TicketCliPath, "/usr/local/bin/ticket", 0, 0, "0755", ct);
            state = ContainerState.Running;
        }
        else if (state == ContainerState.Stopped)
        {
            logger.LogInformation("Starting stopped container {Container}", name);
            await RunOrThrowAsync(["start", name], ct);
            await WaitForNetworkAsync(name, ct);
            state = ContainerState.Running;
        }

        _stateCache[name] = (state, DateTime.UtcNow);
        return state;
    }

    public async Task<ContainerState> GetStateAsync(string name, CancellationToken ct = default)
    {
        if (_stateCache.TryGetValue(name, out var cached) && DateTime.UtcNow - cached.At < StateCacheTtl)
        {
            return cached.State;
        }

        var state = await QueryStateAsync(name, ct);
        _stateCache[name] = (state, DateTime.UtcNow);
        return state;
    }

    public async Task<string?> PullFileAsync(string name, string path, CancellationToken ct = default)
    {
        var result = await RunAsync(["file", "pull", $"{name}{path}", "-"], ct, logFailureAsWarning: false);
        if (!result.Success)
        {
            logger.LogDebug("incus file pull {Container}{Path} failed ({Exit}): {Stderr}", name, path, result.ExitCode, result.Stderr.Trim());
            return null;
        }

        return result.Stdout;
    }

    public async Task PushFileAsync(string name, string path, string content, int uid = 1000, int gid = 1000, string mode = "0644", CancellationToken ct = default)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "dispatch-push-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(tmp, content, new UTF8Encoding(false), ct);
            await PushHostFileAsync(name, tmp, path, uid, gid, mode, ct);
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch (IOException)
            {
            }
        }
    }

    public Task<ExecResult> ExecAsync(string name, IReadOnlyList<string> args, string? cwd, bool asAgent = true, CancellationToken ct = default)
    {
        var full = new List<string> { "exec", name };
        if (asAgent)
        {
            full.AddRange(["--user", "1000", "--group", "1000", "--env", "HOME=/home/agent"]);
        }
        else
        {
            full.AddRange(["--env", "HOME=/root"]);
        }

        if (!string.IsNullOrEmpty(cwd))
        {
            full.AddRange(["--cwd", cwd]);
        }

        full.Add("--");
        full.AddRange(args);
        return RunAsync(full, ct, logFailureAsWarning: true);
    }

    public async Task DeleteAsync(string name, bool snapshot, CancellationToken ct = default)
    {
        if (snapshot)
        {
            var snap = $"pre-delete-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            var res = await RunAsync(["snapshot", "create", name, snap], ct, logFailureAsWarning: true);
            if (!res.Success)
            {
                logger.LogWarning("Snapshot of {Container} failed; deleting anyway", name);
            }
        }

        var del = await RunAsync(["delete", name, "--force"], ct, logFailureAsWarning: true);
        _stateCache[name] = (ContainerState.Missing, DateTime.UtcNow);
        if (!del.Success && !del.Stderr.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            throw new DispatchException("incus_failed", $"incus delete {name} failed: {del.Stderr.Trim()}", 502);
        }
    }

    public IReadOnlyList<string> BuildClaudePrefix(string name, string cwd, IReadOnlyDictionary<string, string> env)
    {
        var prefix = new List<string>
        {
            options.Value.IncusExecutable, "exec", name,
            "--user", "1000", "--group", "1000",
            "--cwd", cwd,
            "--env", "HOME=/home/agent",
            "--env", "TERM=dumb",
        };
        foreach (var (key, value) in env)
        {
            prefix.Add("--env");
            prefix.Add($"{key}={value}");
        }

        prefix.Add("--");
        return prefix;
    }

    public IReadOnlyDictionary<string, string> BuildTicketEnvironment(Ticket ticket, LoadedProject project)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in project.Config.Env)
        {
            env[k] = v;
        }

        env["DISPATCH_URL"] = publicUrl.Resolve();
        env["DISPATCH_TOKEN"] = ticket.Token;
        env["TICKET_ID"] = ticket.Id.ToString();
        env["DISPATCH_PROJECT"] = project.Config.Name;
        return env;
    }

    // ---- internals -------------------------------------------------------

    private async Task PushHostFileAsync(string name, string hostPath, string containerPath, int uid, int gid, string mode, CancellationToken ct)
    {
        if (!File.Exists(hostPath))
        {
            throw new DispatchException("incus_failed", $"Cannot push {hostPath}: file does not exist", 500);
        }

        await RunOrThrowAsync(
            ["file", "push", hostPath, $"{name}{containerPath}", "--uid", uid.ToString(), "--gid", gid.ToString(), "--mode", mode, "-p"],
            ct);
    }

    private async Task<ContainerState> QueryStateAsync(string name, CancellationToken ct)
    {
        var result = await RunAsync(["list", name, "--format", "json"], ct, logFailureAsWarning: true);
        if (!result.Success)
        {
            throw new DispatchException("incus_failed", $"incus list failed: {result.Stderr.Trim()}", 502);
        }

        var info = FindContainer(result.Stdout, name);
        if (info is null)
        {
            return ContainerState.Missing;
        }

        return string.Equals(TryGetString(info.Value, "status"), "Running", StringComparison.OrdinalIgnoreCase)
            ? ContainerState.Running
            : ContainerState.Stopped;
    }

    private async Task WaitForNetworkAsync(string name, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + NetworkWait;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var result = await RunAsync(["list", name, "--format", "json"], ct, logFailureAsWarning: false);
            if (result.Success)
            {
                var info = FindContainer(result.Stdout, name);
                if (info is not null && TryFindIPv4(info.Value) is { } ip)
                {
                    logger.LogInformation("Container {Container} is up with IPv4 {Ip}", name, ip);
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        logger.LogWarning("Container {Container} did not get an IPv4 address within {Seconds}s; continuing", name, NetworkWait.TotalSeconds);
    }

    internal static JsonElement? FindContainer(string listJson, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(listJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (string.Equals(TryGetString(item, "name"), name, StringComparison.Ordinal))
                {
                    return item.Clone();
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>Reads state.network from `incus list --format json`; prefers eth0, ignores lo/docker*/br-*.</summary>
    internal static string? TryFindIPv4(JsonElement container)
    {
        if (!container.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object
            || !state.TryGetProperty("network", out var network) || network.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? Read(JsonProperty nic)
        {
            if (!nic.Value.TryGetProperty("addresses", out var addresses) || addresses.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var a in addresses.EnumerateArray())
            {
                if (TryGetString(a, "family") == "inet" && TryGetString(a, "scope") is null or "global")
                {
                    var addr = TryGetString(a, "address");
                    if (!string.IsNullOrEmpty(addr))
                    {
                        return addr;
                    }
                }
            }

            return null;
        }

        foreach (var nic in network.EnumerateObject())
        {
            if (nic.Name == "eth0" && Read(nic) is { } ip)
            {
                return ip;
            }
        }

        foreach (var nic in network.EnumerateObject())
        {
            if (nic.Name == "lo" || nic.Name.StartsWith("docker", StringComparison.Ordinal) || nic.Name.StartsWith("br-", StringComparison.Ordinal))
            {
                continue;
            }

            if (Read(nic) is { } ip)
            {
                return ip;
            }
        }

        return null;
    }

    private static string? TryGetString(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private async Task RunOrThrowAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var result = await RunAsync(args, ct, logFailureAsWarning: true);
        if (!result.Success)
        {
            throw new DispatchException("incus_failed", $"incus {args[0]} failed ({result.ExitCode}): {result.Stderr.Trim()}", 502);
        }
    }

    private async Task<ExecResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct, bool logFailureAsWarning)
    {
        var psi = new ProcessStartInfo
        {
            FileName = options.Value.IncusExecutable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        logger.LogDebug("incus {Args}", string.Join(' ', args.Select(Quote)));

        var sw = Stopwatch.StartNew();
        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to start {Exe}", options.Value.IncusExecutable);
            return new ExecResult(-1, "", ex.Message);
        }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var result = new ExecResult(proc.ExitCode, stdout, stderr);

        if (!result.Success && logFailureAsWarning)
        {
            logger.LogWarning("incus {Args} exited {Exit} after {Ms}ms: {Stderr}", string.Join(' ', args.Select(Quote)), result.ExitCode, sw.ElapsedMilliseconds, stderr.Trim());
        }
        else
        {
            logger.LogDebug("incus {Cmd} exited {Exit} after {Ms}ms; stdout {StdoutLen}B stderr {StderrLen}B", args[0], result.ExitCode, sw.ElapsedMilliseconds, stdout.Length, stderr.Length);
        }

        return result;
    }

    private static string Quote(string arg) =>
        arg.Contains(' ') || arg.Contains('"') ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;
}
