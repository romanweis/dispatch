using System.Collections.Concurrent;

namespace Dispatch.Api.Config;

public interface IProjectRegistry
{
    IReadOnlyCollection<LoadedProject> All { get; }

    LoadedProject? GetByName(string name);

    LoadedProject? GetById(int id);

    LoadedProject Require(int id);

    void Set(LoadedProject project);
}

public sealed class ProjectRegistry : IProjectRegistry
{
    private readonly ConcurrentDictionary<int, LoadedProject> _byId = new();

    public IReadOnlyCollection<LoadedProject> All => _byId.Values.OrderBy(p => p.Config.Name).ToList();

    public LoadedProject? GetByName(string name) =>
        _byId.Values.FirstOrDefault(p => string.Equals(p.Config.Name, name, StringComparison.Ordinal));

    public LoadedProject? GetById(int id) => _byId.GetValueOrDefault(id);

    public LoadedProject Require(int id) =>
        GetById(id) ?? throw new DispatchException("project_not_loaded", $"Project {id} has no loaded configuration", 500);

    public void Set(LoadedProject project) => _byId[project.Id] = project;
}
