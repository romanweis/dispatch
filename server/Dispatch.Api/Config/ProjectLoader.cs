using System.Text.Json;
using Dispatch.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Dispatch.Api.Config;

/// <summary>Scans &lt;ProjectsDir&gt;/*/project.yaml at startup, upserts project rows, fills the registry.</summary>
public sealed class ProjectLoader(
    IServiceScopeFactory scopeFactory,
    IProjectRegistry registry,
    IOptions<DispatchOptions> options,
    ILogger<ProjectLoader> logger) : IHostedService
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var dir = Path.GetFullPath(options.Value.ProjectsDir);
        if (!Directory.Exists(dir))
        {
            logger.LogWarning("Projects directory {Dir} does not exist; no projects loaded", dir);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();

        foreach (var projectDir in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var yamlPath = Path.Combine(projectDir, "project.yaml");
            if (!File.Exists(yamlPath))
            {
                continue;
            }

            try
            {
                var loaded = await LoadOneAsync(db, projectDir, yamlPath, cancellationToken);
                registry.Set(loaded);
                logger.LogInformation("Loaded project {Name} (id {Id}) from {Path}", loaded.Config.Name, loaded.Id, yamlPath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load project from {Path}", yamlPath);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static ProjectConfig ParseYaml(string yaml)
    {
        var config = Yaml.Deserialize<ProjectConfig>(yaml) ?? throw new InvalidOperationException("project.yaml is empty");
        if (string.IsNullOrWhiteSpace(config.Name))
        {
            throw new InvalidOperationException("project.yaml: 'name' is required");
        }

        if (string.IsNullOrWhiteSpace(config.Workspace))
        {
            throw new InvalidOperationException("project.yaml: 'workspace' is required");
        }

        config.DisplayName = string.IsNullOrWhiteSpace(config.DisplayName) ? config.Name : config.DisplayName;
        config.Org = string.IsNullOrWhiteSpace(config.Org) ? config.Name : config.Org;
        config.MaxParallel = Math.Max(1, config.MaxParallel);
        config.Orchestrator ??= new OrchestratorConfig();
        config.Claude ??= new ClaudeConfig();
        config.Env ??= [];
        config.Repos ??= [];
        return config;
    }

    internal static ProjectPrompts LoadPrompts(string projectDir, ILogger? logger = null)
    {
        string Read(string file, string fallback, bool warn)
        {
            var path = Path.Combine(projectDir, "prompts", file);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }

            if (warn)
            {
                logger?.LogWarning("Prompt template {Path} missing; using built-in default", path);
            }

            return fallback;
        }

        return new ProjectPrompts(
            Read("refine.md", ProjectPrompts.DefaultRefine, warn: true),
            Read("work.md", ProjectPrompts.DefaultWork, warn: true),
            Read("answer.md", ProjectPrompts.DefaultAnswer, warn: false),
            Read("ship.md", ProjectPrompts.DefaultShip, warn: false));
    }

    private async Task<LoadedProject> LoadOneAsync(DispatchDbContext db, string projectDir, string yamlPath, CancellationToken ct)
    {
        var config = ParseYaml(await File.ReadAllTextAsync(yamlPath, ct));
        var prompts = LoadPrompts(projectDir, logger);
        var reposJson = JsonSerializer.Serialize(config.Repos.Select(r => r.Name).ToList());

        var row = await db.Projects.FirstOrDefaultAsync(p => p.Name == config.Name, ct);
        if (row is null)
        {
            row = new Project
            {
                Name = config.Name,
                DisplayName = config.DisplayName,
                Org = config.Org,
                Workspace = config.Workspace,
                BaseContainer = config.BaseContainer,
                MaxParallel = config.MaxParallel,
                ReposJson = reposJson,
                ConfigPath = yamlPath,
                CreatedAt = DateTime.UtcNow,
            };
            db.Projects.Add(row);
        }
        else
        {
            row.DisplayName = config.DisplayName;
            row.Org = config.Org;
            row.Workspace = config.Workspace;
            row.BaseContainer = config.BaseContainer;
            row.MaxParallel = config.MaxParallel;
            row.ReposJson = reposJson;
            row.ConfigPath = yamlPath;
        }

        await db.SaveChangesAsync(ct);
        return new LoadedProject(row.Id, projectDir, config, prompts);
    }
}
