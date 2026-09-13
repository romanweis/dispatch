using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dispatch.Api.Data;

/// <summary>Used by `dotnet ef` only; adding migrations does not need a live database.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DispatchDbContext>
{
    public DispatchDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("DISPATCH_DB")
                 ?? "Host=localhost;Port=5433;Database=dispatch;Username=dispatch;Password=dispatch";
        var options = new DbContextOptionsBuilder<DispatchDbContext>().UseNpgsql(cs).Options;
        return new DispatchDbContext(options);
    }
}
