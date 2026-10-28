using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using ShopVerse.Api.Configuration;

namespace ShopVerse.Api.Health;

public sealed class PostgreSqlHealthCheck : IHealthCheck
{
    private readonly DatabaseOptions _options;

    public PostgreSqlHealthCheck(IOptions<DatabaseOptions> options)
    {
        _options = options.Value;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_options.ConnectionString);
            await conn.OpenAsync(cancellationToken);

            await using var cmd = new NpgsqlCommand("SELECT 1;", conn);
            await cmd.ExecuteScalarAsync(cancellationToken);

            return HealthCheckResult.Healthy("PostgreSQL database connection is healthy.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL database connection failed.", ex);
        }
    }
}
