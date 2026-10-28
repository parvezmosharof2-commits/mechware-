using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using ShopVerse.Api.Configuration;
using StackExchange.Redis;

namespace ShopVerse.Api.Health;

public sealed class RedisHealthCheck : IHealthCheck
{
    private readonly RedisOptions _options;

    public RedisHealthCheck(IOptions<RedisOptions> options)
    {
        _options = options.Value;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = ConfigurationOptions.Parse(_options.ConnectionString);
            config.ConnectTimeout = 2000;
            config.AbortOnConnectFail = false;

            using var redis = await ConnectionMultiplexer.ConnectAsync(config);
            var db = redis.GetDatabase();
            var ping = await db.PingAsync();

            return HealthCheckResult.Healthy($"Redis ping responded in {ping.TotalMilliseconds:F1}ms.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis cluster connection failed.", ex);
        }
    }
}
