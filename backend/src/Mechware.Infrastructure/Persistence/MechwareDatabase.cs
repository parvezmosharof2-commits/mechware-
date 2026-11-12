using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Mechware.Infrastructure.Persistence;

/// <summary>
/// The one place that decides which PostgreSQL database a host talks to, and the deploy-time migration step.
/// The API, the Worker, its commands and the API's readiness check all go through <see cref="ConnectionString"/>.
/// </summary>
public static class MechwareDatabase
{
    /// <summary>Local development default (the dev compose file's database); deployments always set a connection string.</summary>
    public const string LocalDevelopmentConnection = "Host=localhost;Port=5432;Database=mechware;Username=mechware;Password=mechware_secret";

    /// <summary>
    /// <c>ConnectionStrings:DefaultConnection</c>, else the older <c>Database:ConnectionString</c> (still read by the
    /// API's appsettings.json and CI), else the local development default; then <see cref="WithDefaults"/>.
    /// </summary>
    public static string ConnectionString(IConfiguration configuration)
    {
        string? configured = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(configured)) configured = configuration["Database:ConnectionString"];
        return WithDefaults(string.IsNullOrWhiteSpace(configured) ? LocalDevelopmentConnection : configured);
    }

    /// <summary>
    /// Turns GSS (Kerberos) session encryption off unless the connection string asks for it. Npgsql's default (Prefer)
    /// makes every new connection try to load libgssapi_krb5, which the runtime images do not ship ("Cannot load library
    /// libgssapi_krb5.so.2"). Nothing here authenticates with Kerberos; TLS settings are untouched.
    /// </summary>
    public static string WithDefaults(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        bool explicitGss = builder.Keys.Cast<string>()
            .Any(k => string.Equals(k.Replace(" ", string.Empty, StringComparison.Ordinal), "GssEncryptionMode", StringComparison.OrdinalIgnoreCase));
        if (explicitGss) return connectionString;

        return new NpgsqlConnectionStringBuilder(connectionString) { GssEncryptionMode = GssEncryptionMode.Disable }.ConnectionString;
    }

    /// <summary>Session advisory lock held for the whole migration run ("MWMIGRAT").</summary>
    public const long MigrationLockKey = 0x4D574D4947524154;

    /// <summary>
    /// Applies every pending migration and returns a process exit code (0 applied or nothing to do, 1 failed).
    /// Safe to start from several replicas at once: the run holds a PostgreSQL advisory lock, so a second runner waits
    /// and then finds nothing left to apply. EF Core's own lock (Npgsql: <c>LOCK TABLE "__EFMigrationsHistory"</c>) is not
    /// enough on an empty database: concurrent runners there failed with a duplicate key on the history table.
    /// </summary>
    public static async Task<int> MigrateAsync(string connectionString, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        try
        {
            // Unpooled: closing it ends the session, which releases the lock whatever happens below (a pooled connection
            // would keep the session, and the lock, alive in the pool).
            await using var lockConnection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
            await lockConnection.OpenAsync(cancellationToken);
            await using (var acquire = new NpgsqlCommand($"SELECT pg_advisory_lock({MigrationLockKey})", lockConnection) { CommandTimeout = 900 })
            {
                await acquire.ExecuteNonQueryAsync(cancellationToken);
            }

            var options = new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(connectionString).Options;
            await using var db = new MechwareDbContext(options);
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            await output.WriteLineAsync($"Database {target.Database} on {target.Host}:{target.Port}: {pending.Count} pending migration(s).");

            await db.Database.MigrateAsync(cancellationToken);

            var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
            await output.WriteLineAsync($"Up to date: {applied.Count} migration(s) applied, latest {applied.LastOrDefault() ?? "(none)"}.");
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The message only: Npgsql errors never carry the password, and a stack trace adds nothing for an operator.
            await error.WriteLineAsync($"Migration of {target.Database} on {target.Host}:{target.Port} FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
