using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShopVerse.Infrastructure.Persistence;

namespace ShopVerse.IntegrationTests.Support;

/// <summary>
/// The API host over its own throw-away PostgreSQL database. Accounts live in the database now, so every test that
/// signs in needs one; creating it here keeps each test class hermetic. The database exists before the host starts,
/// because the Development seeding runs at startup.
/// </summary>
public class DbBackedFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = "mw_api_" + Guid.NewGuid().ToString("N");
    private readonly string? _environment;
    private readonly IReadOnlyDictionary<string, string> _settings;

    public string ConnectionString { get; }

    public DbBackedFactory(string? environment = null, IReadOnlyDictionary<string, string>? settings = null)
    {
        _environment = environment;
        _settings = settings ?? new Dictionary<string, string>();

        if (!TestInfra.PostgresAvailable)
        {
            ConnectionString = string.Empty;
            return;
        }

        using (var admin = new NpgsqlConnection(TestInfra.AdminConnection))
        {
            admin.Open();
            using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{_dbName}\"", admin);
            cmd.ExecuteNonQuery();
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(TestInfra.AdminConnection) { Database = _dbName }.ConnectionString;
        using var db = new ShopVerseDbContext(new DbContextOptionsBuilder<ShopVerseDbContext>().UseNpgsql(ConnectionString).Options);
        db.Database.EnsureCreated();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (_environment is not null)
        {
            builder.UseEnvironment(_environment);
        }

        builder.UseSetting("ConnectionStrings:DefaultConnection", ConnectionString);
        builder.UseSetting("RateLimiting:GlobalPermitPerMinute", "1000000");
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing || ConnectionString.Length == 0) return;

        NpgsqlConnection.ClearAllPools();
        using var admin = new NpgsqlConnection(TestInfra.AdminConnection);
        admin.Open();
        using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)", admin);
        cmd.ExecuteNonQuery();
    }
}
