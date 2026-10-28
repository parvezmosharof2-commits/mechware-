using System.Net.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using ShopVerse.Api.Configuration;

namespace ShopVerse.Api.Health;

public sealed class MinioHealthCheck : IHealthCheck
{
    private readonly StorageOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;

    public MinioHealthCheck(IOptions<StorageOptions> options, IHttpClientFactory httpClientFactory)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("MinioHealth");
            client.Timeout = TimeSpan.FromSeconds(3);

            var response = await client.GetAsync($"{_options.ServiceUrl.TrimEnd('/')}/minio/health/live", cancellationToken);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy("MinIO S3 storage is healthy.")
                : HealthCheckResult.Degraded($"MinIO returned status code: {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("MinIO S3 storage health check failed.", ex);
        }
    }
}
