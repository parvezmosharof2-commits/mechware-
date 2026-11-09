using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Mechware.Infrastructure.Persistence;

/// <summary>Used by <c>dotnet ef</c> only; connection string comes from <c>MECHWARE_DESIGN_DB</c> or a local default.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MechwareDbContext>
{
    public MechwareDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("MECHWARE_DESIGN_DB")
            ?? "Host=localhost;Port=5432;Database=mechware_design;Username=mechware;Password=mechware_secret";
        var options = new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(conn).Options;
        return new MechwareDbContext(options);
    }
}
