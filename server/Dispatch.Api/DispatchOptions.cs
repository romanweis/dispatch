namespace Dispatch.Api;

public sealed class DispatchOptions
{
    public const string SectionName = "Dispatch";

    /// <summary>Npgsql connection string. Env: DISPATCH_DB.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Kestrel bind URLs (semicolon separated). Env: DISPATCH_BIND_URLS.</summary>
    public string BindUrls { get; set; } = "http://0.0.0.0:9300";

    /// <summary>Directory containing &lt;name&gt;/project.yaml folders. Env: DISPATCH_PROJECTS_DIR.</summary>
    public string ProjectsDir { get; set; } = "../../projects";

    /// <summary>Root of this repository (cli/ticket lives at &lt;RepoDir&gt;/cli/ticket). Env: DISPATCH_REPO_DIR.</summary>
    public string RepoDir { get; set; } = "../..";

    /// <summary>Incus bridge interface whose IPv4 is reachable from containers. Env: DISPATCH_INCUS_BRIDGE.</summary>
    public string IncusBridge { get; set; } = "incusbr0";

    /// <summary>Incus profile applied to ticket containers. Env: DISPATCH_INCUS_PROFILE.</summary>
    public string IncusProfile { get; set; } = "dispatch-agent";

    public string IncusExecutable { get; set; } = "incus";

    /// <summary>Explicit DISPATCH_URL for containers; computed from the bridge IPv4 + bind port when null.</summary>
    public string? PublicUrlForContainers { get; set; }

    /// <summary>Local-dev mode: in-memory incus + simulated claude. Env: DISPATCH_FAKE_INCUS.</summary>
    public bool FakeIncus { get; set; }

    /// <summary>Optional path to the fake-claude test binary (.dll or exe) used in FakeIncus mode. Env: DISPATCH_FAKE_CLAUDE.</summary>
    public string? FakeClaude { get; set; }

    /// <summary>Tests only: do not start the RunQueue background service.</summary>
    public bool DisableRunQueue { get; set; }

    public string TicketCliPath => Path.GetFullPath(Path.Combine(RepoDir, "cli", "ticket"));

    public int BindPort
    {
        get
        {
            var first = BindUrls
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (first is not null)
            {
                var normalized = first.Replace("0.0.0.0", "localhost").Replace("+", "localhost").Replace("*", "localhost");
                if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
                {
                    return uri.Port;
                }
            }

            return 9300;
        }
    }
}
