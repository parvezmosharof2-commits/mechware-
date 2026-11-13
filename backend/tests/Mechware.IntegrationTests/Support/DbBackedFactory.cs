using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mechware.Infrastructure.Persistence;

namespace Mechware.IntegrationTests.Support;

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

    public static readonly string GuestCartTokenKey = Convert.ToBase64String(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("mechware-integration-tests-guest-cart-key")));

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
        using var db = new MechwareDbContext(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(ConnectionString).Options);
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw(AuditChainSql.Install);
        db.Database.ExecuteSqlRaw(PriceListSql.Install);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (_environment is not null)
        {
            builder.UseEnvironment(_environment);
        }

        builder.UseSetting("ConnectionStrings:DefaultConnection", ConnectionString);
        builder.UseSetting("RateLimiting:GlobalPermitPerMinute", "1000000");
        builder.UseSetting("RateLimiting:UserPermitPerMinute", "1000000");
        builder.UseSetting("RateLimiting:IpCeilingPerMinute", "1000000");
        // Production-like hosts refuse to start without a real guest-cart key (E1.2); a test's own settings override it.
        builder.UseSetting("GuestCart:TokenKey", GuestCartTokenKey);
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
