using Dispatch.Api;
using Dispatch.Api.Config;
using Dispatch.Api.Data;
using Dispatch.Api.Incus;
using Dispatch.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dispatch.Api.Tests;

/// <summary>A self-contained service graph on Sqlite in-memory with FakeIncus + simulated claude.</summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    public TestHost()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<DispatchOptions>().Configure(o =>
        {
            o.FakeIncus = true;
            o.ConnectionString = "unused";
        });
        services.AddDbContext<DispatchDbContext>(o => o.UseSqlite(_connection));
        services.AddSingleton<IProjectRegistry, ProjectRegistry>();
        services.AddSingleton<EventBus>();
        services.AddSingleton<RunQueueSignal>();
        services.AddSingleton<RunCancellationRegistry>();
        services.AddSingleton<FakeIncusService>();
        services.AddSingleton<IIncusService>(sp => sp.GetRequiredService<FakeIncusService>());
        services.AddSingleton<IClaudeLauncher>(sp => new FakeClaudeLauncher(null, sp.GetRequiredService<ILogger<FakeClaudeLauncher>>()));
        services.AddScoped<DtoMapper>();
        services.AddScoped<TicketService>();
        services.AddSingleton<RunQueue>();

        Services = services.BuildServiceProvider();

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();
        db.Database.EnsureCreated();

        var project = new Project
        {
            Name = "demo",
            DisplayName = "Demo",
            Org = "demo-org",
            Workspace = "/home/agent/demo",
            BaseContainer = "demo-base",
            MaxParallel = 2,
            ReposJson = "[\"backend\"]",
            ConfigPath = "/projects/demo/project.yaml",
            CreatedAt = DateTime.UtcNow,
        };
        db.Projects.Add(project);
        db.SaveChanges();
        ProjectId = project.Id;

        var config = new ProjectConfig
        {
            Name = "demo",
            DisplayName = "Demo",
            Org = "demo-org",
            Workspace = "/home/agent/demo",
            MaxParallel = 2,
            Repos = [new RepoConfig { Name = "backend", Role = "backend" }],
        };
        var prompts = new ProjectPrompts(
            "REFINE {{ticket.id}} {{ticket.title}}",
            "WORK {{ticket.id}} {{ticket.slug}} {{project.workspace}}",
            ProjectPrompts.DefaultAnswer,
            Plan: "PLAN {{ticket.id}} {{ticket.slug}}");
        Project = new LoadedProject(project.Id, "/projects/demo", config, prompts);
        Services.GetRequiredService<IProjectRegistry>().Set(Project);
    }

    public ServiceProvider Services { get; }

    public int ProjectId { get; }

    public LoadedProject Project { get; }

    public FakeIncusService Incus => Services.GetRequiredService<FakeIncusService>();

    public RunQueue Queue => Services.GetRequiredService<RunQueue>();

    public IServiceScope Scope() => Services.CreateScope();

    public TicketService Tickets(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<TicketService>();

    public DispatchDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<DispatchDbContext>();

    public async Task<Ticket> CreateTicketAsync(string title = "Add login page", string body = "body")
    {
        using var scope = Scope();
        return await Tickets(scope).CreateAsync(ProjectId, title, body);
    }

    public async Task<Ticket> ReloadAsync(long id)
    {
        using var scope = Scope();
        return await Db(scope).Tickets.AsNoTracking().FirstAsync(t => t.Id == id);
    }

    public async Task SetAsync(long id, Action<Ticket> mutate)
    {
        using var scope = Scope();
        var db = Db(scope);
        var ticket = await db.Tickets.FirstAsync(t => t.Id == id);
        mutate(ticket);
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
