using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Api.Health;

/// <summary>
/// Readiness of the database the API actually uses: the probe goes through <see cref="MechwareDbContext"/>, so it can never
/// test a different connection string than the requests do.
/// </summary>
public sealed class PostgreSqlHealthCheck : IHealthCheck
{
    private readonly MechwareDbContext _db;

    public PostgreSqlHealthCheck(MechwareDbContext db)
    {
        _db = db;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await _db.Database.ExecuteSqlRawAsync("SELECT 1;", cancellationToken);
            return HealthCheckResult.Healthy("PostgreSQL database connection is healthy.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL database connection failed.", ex);
        }
    }
}
