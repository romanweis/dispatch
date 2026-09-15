namespace Dispatch.Api.Config;

/// <summary>Mirrors docs/project-config.md (project.yaml).</summary>
public sealed class ProjectConfig
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Org { get; set; } = "";
    public string Workspace { get; set; } = "";
    public int MaxParallel { get; set; } = 1;
    public List<RepoConfig> Repos { get; set; } = [];
    public OrchestratorConfig Orchestrator { get; set; } = new();
    public ClaudeConfig Claude { get; set; } = new();
    public Dictionary<string, string> Env { get; set; } = [];

    public string BaseContainer => $"{Name}-base";

    public string OrchestratorDir => $"{Workspace.TrimEnd('/')}/{Orchestrator.Repo}";
}

public sealed class RepoConfig
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
}

public sealed class OrchestratorConfig
{
    public string Repo { get; set; } = "orchestrator";
    public string DeployWorkflow { get; set; } = "deploy.yml";
    public List<string> RequiredChecks { get; set; } = [];
}

public sealed class ClaudeConfig
{
    public string PermissionMode { get; set; } = "bypassPermissions";
    public List<string> AllowedTools { get; set; } = [];
    public string? Model { get; set; }
    public int MaxTurnsRefine { get; set; } = 60;
    public int MaxTurnsWork { get; set; } = 400;
}

public sealed record ProjectPrompts(string Refine, string Work, string Answer, string Ship = ProjectPrompts.DefaultShip)
{
    public const string DefaultShip =
        """
        Dispatch is asking you to ship ticket #{{ticket.id}} "{{ticket.title}}" (feature {{ticket.id}}, slug {{ticket.slug}}).
        The review gate for this feature is PASSED and the human has authorised the merge for this ticket on the board.

        cd into `{{project.workspace}}/orchestrator` and run `/ship-feature {{ticket.id}}` exactly as that repo's `CLAUDE.md` describes:
        dry run first, then the real merge in the foreground with a long timeout, and never a manual fix if it halts.
        Do not start anything in the background: this session is headless and background processes die when the turn ends.

        Report with `ticket progress shipping`, and when every slice is merged and live, `ticket progress shipped "<repos>"`.
        If the ship halts or rolls back, post `ticket progress halted|rolled-back "<reason>"`, `ticket comment` the incident, and stop.
        If you need a decision from the human, use `ticket ask` and stop.
        """;

    public const string DefaultRefine =
        """
        You are refining ticket #{{ticket.id}} for project {{project.name}} (workspace {{project.workspace}}).

        Title: {{ticket.title}}

        {{ticket.body}}

        Investigate the repositories in the workspace and write an implementation spec.
        If anything is unclear, ask the user with `ticket ask "<question>"` (one call per question) and stop.
        When the spec is complete, submit it with `ticket spec <file>` and stop.
        """;

    public const string DefaultWork =
        """
        Implement ticket #{{ticket.id}} ({{ticket.slug}}) for project {{project.name}}.

        The feature spec is at features/{{ticket.id}}-{{ticket.slug}}.md in this orchestrator repository.
        Follow the workflow in this repository (/start-feature, /review-feature, /ship-feature).
        Report progress with `ticket progress <phase> [note]`.
        """;

    public const string DefaultAnswer =
        """
        The user answered your questions:
        {{answers}}

        Continue the refinement. Submit the final spec with `ticket spec <file>` or ask further questions with `ticket ask`.
        """;
}

/// <summary>A project as loaded from disk plus its database id.</summary>
public sealed record LoadedProject(int Id, string Directory, ProjectConfig Config, ProjectPrompts Prompts);
