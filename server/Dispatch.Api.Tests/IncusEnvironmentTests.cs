using Dispatch.Api.Config;
using Dispatch.Api.Data;
using Dispatch.Api.Incus;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dispatch.Api.Tests;

public sealed class IncusEnvironmentTests
{
    [Fact]
    public void Ticket_environment_raises_the_bash_timeout_cap_and_lets_a_project_override_it()
    {
        var ticket = new Ticket { Title = "t", Token = "tok", ProjectId = 1 };
        var config = new ProjectConfig
        {
            Name = "demo",
            DisplayName = "Demo",
            Org = "demo-org",
            Workspace = "/home/agent/demo",
        };
        var project = new LoadedProject(1, "/projects/demo", config, new ProjectPrompts("r", "w", "a"));
        var service = new IncusService(
            Options.Create(new DispatchOptions()),
            new PublicUrlResolver(Options.Create(new DispatchOptions()), NullLogger<PublicUrlResolver>.Instance),
            NullLogger<IncusService>.Instance);

        var env = service.BuildTicketEnvironment(ticket, project);
        Assert.Equal("5400000", env["BASH_MAX_TIMEOUT_MS"]);

        config.Env["BASH_MAX_TIMEOUT_MS"] = "900000";
        Assert.Equal("900000", service.BuildTicketEnvironment(ticket, project)["BASH_MAX_TIMEOUT_MS"]);
    }
}
