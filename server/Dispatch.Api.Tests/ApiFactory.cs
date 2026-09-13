using Dispatch.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dispatch.Api.Tests;

/// <summary>Boots the real Program with FakeIncus, Sqlite in-memory and a temp projects directory.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public ApiFactory()
    {
        _connection.Open();
        ProjectsDir = Path.Combine(Path.GetTempPath(), "dispatch-tests-" + Guid.NewGuid().ToString("N"));
        var demo = Path.Combine(ProjectsDir, "demo");
        Directory.CreateDirectory(Path.Combine(demo, "prompts"));
        File.WriteAllText(Path.Combine(demo, "project.yaml"),
            """
            name: demo
            displayName: Demo
            org: demo-org
            workspace: /home/agent/demo
            maxParallel: 2
            repos:
              - name: backend
                role: backend
            """);
        File.WriteAllText(Path.Combine(demo, "prompts", "refine.md"), "refine {{ticket.id}}");
        File.WriteAllText(Path.Combine(demo, "prompts", "work.md"), "work {{ticket.id}}");
    }

    public string ProjectsDir { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Dispatch:FakeIncus", "true");
        builder.UseSetting("Dispatch:DisableRunQueue", "true");
        builder.UseSetting("Dispatch:ConnectionString", "unused-in-tests");
        builder.UseSetting("Dispatch:ProjectsDir", ProjectsDir);
        builder.UseSetting("Dispatch:RepoDir", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")));

        // ConfigureTestServices runs after Program's registrations; EF 9+ composes every AddDbContext
        // action via IDbContextOptionsConfiguration<T>, so that has to go as well or both providers apply.
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<DispatchDbContext>>();
            services.RemoveAll<DbContextOptions<DispatchDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<DispatchDbContext>();
            services.AddDbContext<DispatchDbContext>(o => o.UseSqlite(_connection));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
            try
            {
                Directory.Delete(ProjectsDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
