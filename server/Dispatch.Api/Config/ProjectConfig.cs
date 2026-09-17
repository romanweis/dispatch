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

public sealed record ProjectPrompts(
    string Refine,
    string Work,
    string Answer,
    string Ship = ProjectPrompts.DefaultShip,
    string Plan = ProjectPrompts.DefaultPlan,
    string Sync = ProjectPrompts.DefaultSync)
{
    /// <summary>Prepended to every refine and work prompt: the container is a copy of a base image whose clones may be old.</summary>
    public const string DefaultSync =
        """
        Before anything else, get the workspace onto the latest code. This container was copied from a base image whose clones may be days old.

        For every repository in `{{project.workspace}}` (including `orchestrator`):
        - `git -C <repo> fetch origin --prune`
        - if it is on `main` with a clean tree: `git -C <repo> pull --ff-only`
        - if it is on a feature branch of this ticket, leave it as it is; the step that owns that branch rebases it.

        Never force, reset or stash to get past this. If a repo cannot be fast-forwarded, `ticket ask` with the repo and the reason, and stop.
        Only once the workspace is current, continue with the task below.
        """;

    /// <summary>Prepended to the work prompt for task tickets: the agent plans without a human approval round, then runs the normal cycle.</summary>
    public const string DefaultPlan =
        """
        Ticket #{{ticket.id}} "{{ticket.title}}" is a task: it skipped refinement, so nobody has written or approved a plan yet.
        The file features/{{ticket.id}}-{{ticket.slug}}.md in the orchestrator repo holds the human's request as written. Plan first, in this run:

        1. `ticket show`, then explore `{{project.workspace}}` read-only (workspace CLAUDE.md, orchestrator repos.yaml, the affected repos' CLAUDE.md and code).
        2. Rewrite features/{{ticket.id}}-{{ticket.slug}}.md in the shape of docs/plan-template.md. Keep the scope to what the request asks.
           Put the original request verbatim under `## Optional appendix` and list any default assumptions you made there.
        3. `ticket spec features/{{ticket.id}}-{{ticket.slug}}.md`, commit and push the file, `ticket progress planned "<repos>"`.
        4. Do not ask for approval. Only if something material is genuinely ambiguous, or the task is really a multi-goal feature, `ticket ask` with your default and stop.

        Then continue in the same run with the full cycle below (implement, review, fix) exactly as for a refined feature.
        """;

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
