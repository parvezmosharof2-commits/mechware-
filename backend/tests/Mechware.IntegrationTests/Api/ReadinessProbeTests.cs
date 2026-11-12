using System.Net;
using System.Text.Json;
using FluentAssertions;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Api;

/// <summary>
/// H3a.2: /health/ready reports what the API really depends on. The database probe uses the DbContext's connection
/// (ConnectionStrings:DefaultConnection), never a second setting, and object storage is probed only when it is configured.
/// </summary>
public sealed class ReadinessProbeTests
{
    // Something that would fail if the probe read it: the older Database:ConnectionString setting.
    private const string WrongDatabase = "Host=127.0.0.1;Port=1;Database=not_this_one;Username=nobody;Password=nothing;Timeout=2";

    private static DbBackedFactory Factory(params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string>
        {
            ["Database:ConnectionString"] = WrongDatabase,
            ["ConnectionStrings:Redis"] = TestInfra.RedisConnection
        };
        foreach (var (key, value) in extra) settings[key] = value;
        return new DbBackedFactory("Development", settings);
    }

    private static async Task<(HttpStatusCode Status, Dictionary<string, string> Checks)> ReadyAsync(DbBackedFactory factory)
    {
        var response = await factory.CreateClient().GetAsync("/health/ready");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var checks = json.RootElement.GetProperty("checks").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("status").GetString()!);
        return (response.StatusCode, checks);
    }

    [PostgresAndRedisFact]
    public async Task Ready_ProbesTheDbContextDatabase_AndNoObjectStorageWhenNoneIsConfigured()
    {
        await using var factory = Factory();

        var (status, checks) = await ReadyAsync(factory);

        checks.Should().BeEquivalentTo(new Dictionary<string, string> { ["postgresql"] = "Healthy", ["redis"] = "Healthy" });
        status.Should().Be(HttpStatusCode.OK);
    }

    [PostgresAndRedisFact]
    public async Task Ready_ProbesObjectStorage_OnceItsUrlIsConfigured()
    {
        // Port 9 (discard) is closed: the configured store is unreachable, so readiness must say so.
        await using var factory = Factory(
            ("Storage:ServiceUrl", "http://127.0.0.1:9"), ("Storage:AccessKey", "test-access"), ("Storage:SecretKey", "test-secret"));

        var (status, checks) = await ReadyAsync(factory);

        checks.Should().ContainKey("minio").WhoseValue.Should().Be("Unhealthy");
        checks["postgresql"].Should().Be("Healthy");
        status.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [PostgresFact]
    public async Task Startup_Fails_WhenObjectStorageIsConfiguredWithoutKeys()
    {
        // appsettings.Development.json has local MinIO keys; blank them so only the URL is set.
        await using var factory = Factory(("Storage:ServiceUrl", "http://minio:9000"), ("Storage:AccessKey", ""), ("Storage:SecretKey", ""));

        FluentActions.Invoking(() => factory.CreateClient()).Should().Throw<Exception>()
            .Where(e => e.ToString().Contains("Storage:ServiceUrl is set"));
    }
}
