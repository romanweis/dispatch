using Dispatch.Api.Data;
using Dispatch.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Api.Endpoints;

public static class ProjectEndpoints
{
    public static RouteGroupBuilder MapProjectEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/projects");

        group.MapGet("", async (DispatchDbContext db, CancellationToken ct) =>
        {
            var projects = await db.Projects.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
            return Results.Ok(projects.Select(DtoMapper.ToDto).ToList());
        });

        return group;
    }
}
