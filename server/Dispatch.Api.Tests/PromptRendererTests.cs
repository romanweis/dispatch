using Dispatch.Api.Config;
using Dispatch.Api.Data;
using Dispatch.Api.Services;
using Xunit;

namespace Dispatch.Api.Tests;

public sealed class PromptRendererTests
{
    private static readonly ProjectConfig Project = new()
    {
        Name = "bognerchess",
        Org = "bognerchess-org",
        Workspace = "/home/agent/bognerchess",
    };

    private static Ticket NewTicket() => new()
    {
        Id = 17,
        Title = "Add trainer calendar",
        Body = "We need a calendar.\nWith slots.",
        Spec = "## Spec",
        Slug = "add-trainer-calendar-x1z",
        Token = "t",
    };

    [Fact]
    public void Replaces_all_documented_placeholders()
    {
        const string template =
            "{{ticket.id}}|{{ticket.title}}|{{ticket.body}}|{{ticket.spec}}|{{ticket.slug}}|{{project.name}}|{{project.workspace}}|{{project.org}}";

        var rendered = PromptRenderer.Render(template, NewTicket(), Project);

        Assert.Equal(
            "17|Add trainer calendar|We need a calendar.\nWith slots.|## Spec|add-trainer-calendar-x1z|bognerchess|/home/agent/bognerchess|bognerchess-org",
            rendered);
    }

    [Fact]
    public void Tolerates_whitespace_inside_braces_and_unknown_placeholders()
    {
        var rendered = PromptRenderer.Render("a {{ ticket.id }} b {{nope}} c", NewTicket(), Project);
        Assert.Equal("a 17 b  c", rendered);
    }

    [Fact]
    public void Null_spec_and_slug_render_empty()
    {
        var ticket = NewTicket();
        ticket.Spec = null;
        ticket.Slug = null;
        Assert.Equal("[][]", PromptRenderer.Render("[{{ticket.spec}}][{{ticket.slug}}]", ticket, Project));
    }

    [Fact]
    public void Answers_render_as_q_a_blocks()
    {
        var answers = new List<Question>
        {
            new() { Text = "Which DB?", Answer = "Postgres" },
            new() { Text = "Auth?", Answer = "Keycloak" },
        };

        var rendered = PromptRenderer.Render(ProjectPrompts.DefaultAnswer, NewTicket(), Project, answers: answers);

        Assert.StartsWith("The user answered your questions:\nQ: Which DB?\nA: Postgres\n\nQ: Auth?\nA: Keycloak\n", rendered);
    }

    [Fact]
    public void Questions_render_as_bullets()
    {
        var questions = new List<Question> { new() { Text = "One" }, new() { Text = "Two" } };
        Assert.Equal("- One\n- Two", PromptRenderer.Render("{{questions}}", NewTicket(), Project, questions: questions));
    }

    [Fact]
    public void Project_yaml_parses_per_contract()
    {
        const string yaml =
            """
            name: bognerchess
            displayName: Bogner Chess
            org: bognerchess
            workspace: /home/agent/bognerchess
            maxParallel: 3
            repos:
              - name: backend
                role: backend
              - name: academy
                role: astro
            orchestrator:
              repo: orchestrator
              deployWorkflow: deploy.yml
              requiredChecks: []
            claude:
              permissionMode: bypassPermissions
              allowedTools: []
              model: null
              maxTurnsRefine: 60
              maxTurnsWork: 400
            env:
              ASPNETCORE_ENVIRONMENT: Development
            """;

        var config = ProjectLoader.ParseYaml(yaml);

        Assert.Equal("bognerchess", config.Name);
        Assert.Equal("Bogner Chess", config.DisplayName);
        Assert.Equal(3, config.MaxParallel);
        Assert.Equal(2, config.Repos.Count);
        Assert.Equal("astro", config.Repos[1].Role);
        Assert.Equal("bognerchess-base", config.BaseContainer);
        Assert.Equal("/home/agent/bognerchess/orchestrator", config.OrchestratorDir);
        Assert.Null(config.Claude.Model);
        Assert.Equal(400, config.Claude.MaxTurnsWork);
        Assert.Equal("Development", config.Env["ASPNETCORE_ENVIRONMENT"]);
    }
}
