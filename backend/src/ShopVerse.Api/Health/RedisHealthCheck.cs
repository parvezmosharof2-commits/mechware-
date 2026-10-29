using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace ShopVerse.Api.Health;

public sealed class RedisHealthCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer _multiplexer;

    public RedisHealthCheck(IConnectionMultiplexer multiplexer)
    {
        _multiplexer = multiplexer;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_multiplexer.IsConnected)
            {
                return HealthCheckResult.Unhealthy("Redis multiplexer is disconnected.");
            }

            var db = _multiplexer.GetDatabase();
            var latency = await db.PingAsync().WaitAsync(cancellationToken);

            return HealthCheckResult.Healthy($"Redis ping responded in {latency.TotalMilliseconds:F1}ms.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis cluster connection failed.", ex);
        }
    }
}
