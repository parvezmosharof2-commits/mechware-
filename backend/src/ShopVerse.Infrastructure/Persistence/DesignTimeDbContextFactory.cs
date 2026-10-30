using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ShopVerse.Infrastructure.Persistence;

/// <summary>Used by <c>dotnet ef</c> only; connection string comes from <c>SHOPVERSE_DESIGN_DB</c> or a local default.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ShopVerseDbContext>
{
    public ShopVerseDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("SHOPVERSE_DESIGN_DB")
            ?? "Host=localhost;Port=5432;Database=shopverse_design;Username=shopverse;Password=shopverse_secret";
        var options = new DbContextOptionsBuilder<ShopVerseDbContext>().UseNpgsql(conn).Options;
        return new ShopVerseDbContext(options);
    }
}
