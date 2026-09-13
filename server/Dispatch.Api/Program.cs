using System.Text.Json;
using System.Text.Json.Serialization;
using Dispatch.Api;
using Dispatch.Api.Config;
using Dispatch.Api.Data;
using Dispatch.Api.Endpoints;
using Dispatch.Api.Incus;
using Dispatch.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Plain DISPATCH_* environment variables map onto the Dispatch section (Dispatch__X still works via the default provider).
// Inserted before the command-line source so `--Dispatch:X=` on the command line keeps the highest priority.
var envMap = new Dictionary<string, string?>();
void MapEnv(string env, string key)
{
    var value = Environment.GetEnvironmentVariable(env);
    if (!string.IsNullOrEmpty(value))
    {
        envMap[$"{DispatchOptions.SectionName}:{key}"] = value;
    }
}

MapEnv("DISPATCH_DB", nameof(DispatchOptions.ConnectionString));
MapEnv("DISPATCH_BIND_URLS", nameof(DispatchOptions.BindUrls));
MapEnv("DISPATCH_PROJECTS_DIR", nameof(DispatchOptions.ProjectsDir));
MapEnv("DISPATCH_REPO_DIR", nameof(DispatchOptions.RepoDir));
MapEnv("DISPATCH_INCUS_BRIDGE", nameof(DispatchOptions.IncusBridge));
MapEnv("DISPATCH_INCUS_PROFILE", nameof(DispatchOptions.IncusProfile));
MapEnv("DISPATCH_INCUS_EXECUTABLE", nameof(DispatchOptions.IncusExecutable));
MapEnv("DISPATCH_PUBLIC_URL", nameof(DispatchOptions.PublicUrlForContainers));
MapEnv("DISPATCH_FAKE_INCUS", nameof(DispatchOptions.FakeIncus));
MapEnv("DISPATCH_FAKE_CLAUDE", nameof(DispatchOptions.FakeClaude));
if (envMap.Count > 0)
{
    var sources = builder.Configuration.Sources;
    var cmdIndex = -1;
    for (var i = 0; i < sources.Count; i++)
    {
        if (sources[i] is CommandLineConfigurationSource)
        {
            cmdIndex = i;
            break;
        }
    }

    sources.Insert(cmdIndex < 0 ? sources.Count : cmdIndex, new MemoryConfigurationSource { InitialData = envMap });
}

builder.Services.AddOptions<DispatchOptions>()
    .Bind(builder.Configuration.GetSection(DispatchOptions.SectionName));

var dispatch = builder.Configuration.GetSection(DispatchOptions.SectionName).Get<DispatchOptions>() ?? new DispatchOptions();
if (!string.IsNullOrWhiteSpace(dispatch.BindUrls) && builder.Environment.EnvironmentName != "Testing")
{
    builder.WebHost.UseUrls(dispatch.BindUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});

builder.Services.AddDbContext<DispatchDbContext>((sp, o) =>
{
    var cs = sp.GetRequiredService<IOptions<DispatchOptions>>().Value.ConnectionString;
    if (string.IsNullOrWhiteSpace(cs))
    {
        throw new InvalidOperationException("Dispatch:ConnectionString (env DISPATCH_DB) is not configured");
    }

    o.UseNpgsql(cs);
});

builder.Services.AddSingleton<IProjectRegistry, ProjectRegistry>();
builder.Services.AddSingleton<EventBus>();
builder.Services.AddSingleton<RunQueueSignal>();
builder.Services.AddSingleton<RunCancellationRegistry>();
builder.Services.AddSingleton<PublicUrlResolver>();
builder.Services.AddScoped<DtoMapper>();
builder.Services.AddScoped<TicketService>();
builder.Services.AddExceptionHandler<DispatchExceptionHandler>();
builder.Services.AddProblemDetails();

if (dispatch.FakeIncus)
{
    builder.Services.AddSingleton<FakeIncusService>();
    builder.Services.AddSingleton<IIncusService>(sp => sp.GetRequiredService<FakeIncusService>());
    builder.Services.AddSingleton<IClaudeLauncher>(sp => new FakeClaudeLauncher(
        sp.GetRequiredService<IOptions<DispatchOptions>>().Value.FakeClaude,
        sp.GetRequiredService<ILogger<FakeClaudeLauncher>>()));
}
else
{
    builder.Services.AddSingleton<IIncusService, IncusService>();
    builder.Services.AddSingleton<IClaudeLauncher, ClaudeSessionLauncher>();
}

// Order matters: projects must be loaded before the queue starts dispatching runs.
builder.Services.AddHostedService<ProjectLoader>();
builder.Services.AddSingleton<RunQueue>();
if (!dispatch.DisableRunQueue)
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RunQueue>());
}

var app = builder.Build();

// Schema: migrations on PostgreSQL; EnsureCreated for other providers (tests).
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();
    if (db.Database.IsNpgsql())
    {
        await db.Database.MigrateAsync();
    }
    else
    {
        await db.Database.EnsureCreatedAsync();
    }
}

app.UseExceptionHandler();

var api = app.MapGroup("/api");
api.MapProjectEndpoints();
api.MapTicketEndpoints();
api.MapRunEndpoints();
api.MapEventEndpoints();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new ErrorDto("not found", "not_found"));
        return;
    }

    var index = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "index.html");
    if (!File.Exists(index))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync("UI not deployed (wwwroot/index.html missing)");
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(index);
});

app.Logger.LogInformation(
    "Dispatch API starting (FakeIncus={Fake}, ProjectsDir={Projects}, RepoDir={Repo})",
    dispatch.FakeIncus, Path.GetFullPath(dispatch.ProjectsDir), Path.GetFullPath(dispatch.RepoDir));

app.Run();

/// <summary>Exposed for WebApplicationFactory in tests.</summary>
public partial class Program;
