using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Persistence;

/// <summary>
/// H3a.2: `Mechware.Worker migrate`, the deploy step. Every migration applies to an empty database, several replicas may
/// run it at the same time (EF Core's migration lock serialises them), and a failure is a non-zero exit code.
/// </summary>
public sealed class MigrateCommandTests
{
    [PostgresFact]
    public async Task ConcurrentRunners_OnAnEmptyDatabase_AllSucceed_AndApplyEveryMigrationOnce()
    {
        string name = "mw_migrate_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(TestInfra.AdminConnection))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        string cs = MechwareDatabase.WithDefaults(new NpgsqlConnectionStringBuilder(TestInfra.AdminConnection) { Database = name }.ConnectionString);
        try
        {
            var outputs = Enumerable.Range(0, 3).Select(_ => (Out: new StringWriter(), Err: new StringWriter())).ToList();
            int[] exitCodes = await Task.WhenAll(outputs.Select(o => Task.Run(() => MechwareDatabase.MigrateAsync(cs, o.Out, o.Err))));

            exitCodes.Should().AllBeEquivalentTo(0, string.Join("\n", outputs.Select(o => o.Err.ToString())));
            await using var db = new MechwareDbContext(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(cs).Options);
            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
            (await db.Database.GetAppliedMigrationsAsync()).Should().Equal(db.Database.GetMigrations());
            (await db.DataProtectionKeys.CountAsync()).Should().Be(0, "the latest migration's table exists");

            // The advisory lock was released: the next deploy is not blocked.
            await using var probe = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString);
            await probe.OpenAsync();
            await using var tryLock = new NpgsqlCommand($"SELECT pg_try_advisory_lock({MechwareDatabase.MigrationLockKey})", probe);
            ((bool)(await tryLock.ExecuteScalarAsync())!).Should().BeTrue();
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(TestInfra.AdminConnection);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task UnreachableDatabase_ExitsNonZero_WithoutPrintingThePassword()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        int exit = await MechwareDatabase.MigrateAsync(
            "Host=127.0.0.1;Port=1;Database=nowhere;Username=u;Password=do-not-print-me;Timeout=2", output, error);

        exit.Should().Be(1);
        error.ToString().Should().Contain("FAILED").And.Contain("nowhere").And.NotContain("do-not-print-me");
    }
}
