using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShopVerse.Infrastructure.Persistence;
using ShopVerse.Infrastructure.Persistence.Outbox;
using StackExchange.Redis;

namespace ShopVerse.IntegrationTests.Support;

public static class TestInfra
{
    public static readonly string AdminConnection =
        Environment.GetEnvironmentVariable("MECHWARE_TEST_PG")
        ?? "Host=localhost;Port=5432;Database=postgres;Username=shopverse;Password=shopverse_secret;Maximum Pool Size=150;Pooling=true";

    public static readonly string RedisConnection =
        Environment.GetEnvironmentVariable("MECHWARE_TEST_REDIS") ?? "localhost:6379,abortConnect=false,connectTimeout=1000";

    public static bool PostgresAvailable { get; } = Probe(() =>
    {
        using var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(AdminConnection) { Timeout = 2 }.ConnectionString);
        conn.Open();
    });

    public static bool RedisAvailable { get; } = Probe(() =>
    {
        using var mux = ConnectionMultiplexer.Connect(RedisConnection);
        if (!mux.IsConnected) throw new InvalidOperationException("redis down");
    });

    private static bool Probe(Action action)
    {
        try { action(); return true; } catch { return false; }
    }
}

/// <summary>[Fact] that skips (instead of failing) when no local PostgreSQL is reachable. CI provides one.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!TestInfra.PostgresAvailable) Skip = "PostgreSQL not reachable (set MECHWARE_TEST_PG).";
    }
}

public sealed class PostgresAndRedisFactAttribute : FactAttribute
{
    public PostgresAndRedisFactAttribute()
    {
        if (!TestInfra.PostgresAvailable) Skip = "PostgreSQL not reachable (set MECHWARE_TEST_PG).";
        else if (!TestInfra.RedisAvailable) Skip = "Redis not reachable (set MECHWARE_TEST_REDIS).";
    }
}

/// <summary>Creates a throw-away database from the EF model so every test class is isolated.</summary>
public sealed class PostgresDatabaseFixture : IAsyncLifetime
{
    private string _dbName = string.Empty;

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        if (!TestInfra.PostgresAvailable) return;

        _dbName = "mw_test_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(TestInfra.AdminConnection))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{_dbName}\"", admin);
            await cmd.ExecuteNonQueryAsync();
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(TestInfra.AdminConnection) { Database = _dbName }.ConnectionString;
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
    }

    public ShopVerseDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ShopVerseDbContext>()
            .UseNpgsql(ConnectionString)
            .AddInterceptors(new DomainEventOutboxInterceptor())
            .Options;
        return new ShopVerseDbContext(options);
    }

    public async Task DisposeAsync()
    {
        if (_dbName.Length == 0) return;
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(TestInfra.AdminConnection);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)", admin);
        await cmd.ExecuteNonQueryAsync();
    }
}
