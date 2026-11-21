using System.Diagnostics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Mechware.Application.Invoicing;
using Mechware.Domain.Identity;
using Mechware.Domain.Invoicing;
using Mechware.Domain.Sellers;
using Mechware.Infrastructure.Invoicing;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Invoicing;

/// <summary>
/// E6.2, threat model §১৪ IT1 (numbering part), IT10, IT11: the counter row, its lock, the database clock and the guard trigger.
/// Some tests pin the database clock: a function <c>mw_test_clock.clock_timestamp()</c> placed before <c>pg_catalog</c> on
/// the transaction's search path answers a set instant to the allocator's unqualified <c>clock_timestamp()</c> call; the
/// allocator's SQL is unchanged. Each test uses its own seller, so the series never meet.
/// </summary>
[Collection(ExclusiveTimingCollection.Name)]
public class InvoiceNumberAllocatorTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string LastMigrationBefore = "20261120233653_AddReturnRequests";

    private static InvoiceNumberAllocator Allocator(MechwareDbContext db) => new(db, NullLogger<InvoiceNumberAllocator>.Instance);

    private static DateTime Utc(string iso) => DateTime.SpecifyKind(DateTime.Parse(iso, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);

    private async Task<Guid> SellerAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        await using var db = fixture.CreateContext();
        var user = User.Create($"invoicing-{suffix}@example.test", null, null, "Invoicing Seller");
        var seller = new Seller(Guid.NewGuid(), user.Id, $"Invoicing Seller {suffix}", $"invoicing-{suffix}", null, null, null, 4.5m, 10, 99m, DateTime.UtcNow);
        db.Users.Add(user);
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        return seller.Id;
    }

    private async Task InstallTestClockAsync()
    {
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE SCHEMA IF NOT EXISTS mw_test_clock;
            CREATE OR REPLACE FUNCTION mw_test_clock.clock_timestamp() RETURNS timestamptz
                LANGUAGE sql STABLE AS $$ SELECT current_setting('mw_test.now')::timestamptz $$;
            """);
    }

    private static async Task PinClockAsync(MechwareDbContext db, DateTime utc)
    {
        await db.Database.ExecuteSqlRawAsync("SET LOCAL search_path = mw_test_clock, public, pg_catalog");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('mw_test.now', {utc.ToString("O")}, true)");
    }

    /// <summary>One issue in its own transaction: take the number, then commit (or roll back).</summary>
    private async Task<TakenInvoiceNumber> TakeAsync(Guid seller, InvoiceSeries series = InvoiceSeries.TaxInvoice, DateTime? at = null, bool commit = true)
    {
        await using var db = fixture.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        if (at is { } t) await PinClockAsync(db, t);
        var taken = await Allocator(db).TakeAsync(seller, series);
        if (commit) await tx.CommitAsync();
        else await tx.RollbackAsync();
        return taken;
    }

    private async Task<(int Year, long Next, DateTime? Last)> CounterAsync(Guid seller, InvoiceSeries series = InvoiceSeries.TaxInvoice)
    {
        await using var db = fixture.CreateContext();
        var c = await db.InvoiceNumberCounters.AsNoTracking().SingleAsync(x => x.SellerId == seller && x.Series == series);
        return (c.Year, c.NextSerial, c.LastIssuedAtUtc);
    }

    private static async Task<DateTime> DbClockAsync(MechwareDbContext db) =>
        (await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").ToListAsync()).Single();

    private static PostgresException Pg(Exception ex) =>
        ex as PostgresException ?? ex.InnerException as PostgresException ?? throw new Xunit.Sdk.XunitException($"Expected a PostgreSQL error, got {ex}");

    private static async Task<List<T>> InParallelAsync<T>(int count, Func<int, Task<T>> work)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, count).Select(async i => { await gate.Task; return await work(i); }).ToList();
        gate.SetResult();
        return [.. await Task.WhenAll(tasks)];
    }

    // ---- IT1: no gap, no duplicate ------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task ParallelIssuesGetConsecutiveNumbers_WithNoGaps()
    {
        var seller = await SellerAsync();
        var taken = await InParallelAsync(200, _ => TakeAsync(seller));

        taken.Select(t => t.Serial).Order().Should().Equal(Enumerable.Range(1, 200).Select(i => (long)i), "200 parallel issues take 1…200, no gap, no duplicate");
        taken.Select(t => t.Format("MW")).Distinct().Should().HaveCount(200);
        taken.Select(t => t.FiscalYear).Distinct().Should().ContainSingle();
        (await CounterAsync(seller)).Next.Should().Be(201);
    }

    [PostgresFact]
    public async Task ARolledBackIssueLeavesNoGap()
    {
        var seller = await SellerAsync();
        (await TakeAsync(seller)).Serial.Should().Be(1);
        (await TakeAsync(seller, commit: false)).Serial.Should().Be(2);
        await using (var db = fixture.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            (await Allocator(db).TakeAsync(seller, InvoiceSeries.TaxInvoice)).Serial.Should().Be(2);
            // The issue fails after the number was taken (the caller's insert, a validation): disposing without COMMIT rolls back.
        }
        (await CounterAsync(seller)).Next.Should().Be(2, "both rolled-back increments were undone");
        (await TakeAsync(seller)).Serial.Should().Be(2);
    }

    [PostgresFact]
    public async Task AConnectionLostBeforeCommit_LeavesNoGap_AndTheNextIssueTakesTheSameSerial()
    {
        var seller = await SellerAsync();
        (await TakeAsync(seller)).Serial.Should().Be(1);

        await using (var db = fixture.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            (await Allocator(db).TakeAsync(seller, InvoiceSeries.TaxInvoice)).Serial.Should().Be(2);
            var pid = (await db.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").ToListAsync()).Single();
            await using (var killer = fixture.CreateContext())
                await killer.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_terminate_backend({pid})");
            await FluentActions.Invoking(() => tx.CommitAsync()).Should().ThrowAsync<Exception>("the server ended the session before COMMIT");
        }
        (await CounterAsync(seller)).Next.Should().Be(2);
        (await TakeAsync(seller)).Serial.Should().Be(2, "the lost issue gave its serial back");
    }

    [PostgresFact]
    public async Task TheCounterCanOnlyStepByOne_FailsAtDbLevelOtherwise()
    {
        var seller = await SellerAsync();
        await TakeAsync(seller);
        var second = await TakeAsync(seller);
        var fy = second.FiscalYear;
        string s = nameof(InvoiceSeries.TaxInvoice);
        var other = await SellerAsync();

        await using var db = fixture.CreateContext();
        var refused = new (string Why, FormattableString Sql)[]
        {
            ("skip a serial", $"UPDATE invoice_number_counters SET next_serial = next_serial + 2, last_issued_at = clock_timestamp() WHERE seller_id = {seller} AND series = {s}"),
            ("decrement", $"UPDATE invoice_number_counters SET next_serial = next_serial - 1, last_issued_at = clock_timestamp() WHERE seller_id = {seller} AND series = {s}"),
            ("reuse a serial", $"UPDATE invoice_number_counters SET last_issued_at = clock_timestamp() WHERE seller_id = {seller} AND series = {s}"),
            ("go back a year", $"UPDATE invoice_number_counters SET fiscal_year = {fy - 1}, next_serial = 2, last_issued_at = clock_timestamp() WHERE seller_id = {seller} AND series = {s}"),
            ("roll over without serial 1", $"UPDATE invoice_number_counters SET fiscal_year = {fy + 1}, next_serial = 3, last_issued_at = clock_timestamp() + interval '1 year' WHERE seller_id = {seller} AND series = {s}"),
            ("a year that is not the Dhaka year of the issue", $"UPDATE invoice_number_counters SET fiscal_year = {fy + 1}, next_serial = 2, last_issued_at = clock_timestamp() WHERE seller_id = {seller} AND series = {s}"),
            ("an issue instant going back", $"UPDATE invoice_number_counters SET next_serial = next_serial + 1, last_issued_at = last_issued_at - interval '1 second' WHERE seller_id = {seller} AND series = {s}"),
            ("move to another series", $"UPDATE invoice_number_counters SET series = 'CreditNote', next_serial = next_serial + 1, last_issued_at = clock_timestamp() WHERE seller_id = {seller} AND series = {s}"),
            ("move to another seller", $"UPDATE invoice_number_counters SET seller_id = {other}, next_serial = next_serial + 1, last_issued_at = clock_timestamp() WHERE seller_id = {seller} AND series = {s}"),
            ("start a series mid-way", $"INSERT INTO invoice_number_counters (seller_id, series, fiscal_year, next_serial, last_issued_at) VALUES ({other}, {s}, {fy}, 40, clock_timestamp())"),
            ("delete", $"DELETE FROM invoice_number_counters WHERE seller_id = {seller} AND series = {s}"),
            ("truncate", $"TRUNCATE invoice_number_counters")
        };
        foreach (var (why, sql) in refused)
            Pg(await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(sql))).SqlState
                .Should().Be(PostgresErrorCodes.IntegrityConstraintViolation, why);

        (await CounterAsync(seller)).Should().Be((fy, 3L, (DateTime?)second.IssuedAtUtc), "nothing above changed the row");

        // Control: the one step the guard allows by hand, rolled back so the series stays as issued.
        await using var tx = await db.Database.BeginTransactionAsync();
        (await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE invoice_number_counters SET next_serial = next_serial + 1, last_issued_at = clock_timestamp() WHERE seller_id = {seller} AND series = {s}"))
            .Should().Be(1);
        await tx.RollbackAsync();
    }

    [PostgresFact]
    public async Task TheAllocator_RefusesToRunOutsideATransaction()
    {
        var seller = await SellerAsync();
        await using var db = fixture.CreateContext();
        await FluentActions.Invoking(() => Allocator(db).TakeAsync(seller, InvoiceSeries.TaxInvoice))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*inside the issuing transaction*");
        (await db.InvoiceNumberCounters.CountAsync(c => c.SellerId == seller)).Should().Be(0, "nothing was written");
    }

    [PostgresFact]
    public async Task InvoiceAndCreditNoteSeries_AreIndependent_PerSellerAndMode()
    {
        var seller = await SellerAsync();
        var other = await SellerAsync();
        var order = new[]
        {
            (seller, InvoiceSeries.TaxInvoice), (seller, InvoiceSeries.TaxInvoice), (seller, InvoiceSeries.CreditNote),
            (other, InvoiceSeries.TaxInvoice), (seller, InvoiceSeries.TaxInvoice), (seller, InvoiceSeries.SpecimenTaxInvoice),
            (seller, InvoiceSeries.CreditNote), (seller, InvoiceSeries.SpecimenCreditNote)
        };
        var numbers = new List<string>();
        foreach (var (s, series) in order) numbers.Add((await TakeAsync(s, series)).Format("MW"));

        var fy = FiscalYear.Label(FiscalYear.Of(DateTime.UtcNow));
        numbers.Should().Equal(
            $"MW-INV-{fy}-000001", $"MW-INV-{fy}-000002", $"MW-CN-{fy}-000001", $"MW-INV-{fy}-000001",
            $"MW-INV-{fy}-000003", $"SPEC-INV-{fy}-000001", $"MW-CN-{fy}-000002", $"SPEC-CN-{fy}-000001");
    }

    // ---- IT10: the fiscal year on the Dhaka date ---------------------------------------------------------------------------

    [PostgresFact]
    public async Task AnIssueAtTheLastSecondOfJune30Dhaka_IsInTheOldYear_AndTheNextSecondStartsAtOne()
    {
        await InstallTestClockAsync();
        var seller = await SellerAsync();
        (await TakeAsync(seller, at: Utc("2027-06-30T17:59:58"))).Format("MW").Should().Be("MW-INV-2026-27-000001");
        (await TakeAsync(seller, at: Utc("2027-06-30T17:59:59.999999"))).Format("MW").Should().Be("MW-INV-2026-27-000002",
            "30 June 23:59:59 Dhaka is the old year");
        (await TakeAsync(seller, at: Utc("2027-06-30T18:00:00"))).Format("MW").Should().Be("MW-INV-2027-28-000001",
            "1 July 00:00 Dhaka starts the new year at 1, although the UTC date is still 30 June");
        (await TakeAsync(seller, at: Utc("2027-06-30T18:00:01"))).Format("MW").Should().Be("MW-INV-2027-28-000002");
        (await CounterAsync(seller)).Should().Be((2027, 3L, (DateTime?)Utc("2027-06-30T18:00:01")));
    }

    [PostgresFact]
    public async Task TheYearRollover_HappensOnceUnderParallelIssues()
    {
        await InstallTestClockAsync();
        var seller = await SellerAsync();
        for (var i = 0; i < 3; i++) await TakeAsync(seller, at: Utc("2028-06-30T17:00:00").AddSeconds(i));

        var taken = await InParallelAsync(60, i => TakeAsync(seller, at: Utc("2028-06-30T18:00:05")));

        taken.Should().OnlyContain(t => t.FiscalYear == 2028);
        taken.Select(t => t.Serial).Order().Should().Equal(Enumerable.Range(1, 60).Select(i => (long)i), "exactly one issue rolled the year over to 1");
        (await CounterAsync(seller)).Should().Be((2028, 61L, (DateTime?)Utc("2028-06-30T18:00:05")));
    }

    // ---- IT11: the issue instant is the database clock after the lock -------------------------------------------------------

    [PostgresFact]
    public async Task TheIssueInstant_ComesFromTheDatabaseClock_AfterTheCounterLock()
    {
        var seller = await SellerAsync();
        await TakeAsync(seller);

        // The first issuer is between step 2 (row locked) and its update, the window where a second issuer has already passed
        // step 1 and waits on the lock; it then takes its own number and commits.
        await using var holder = fixture.CreateContext();
        await using var holding = await holder.Database.BeginTransactionAsync();
        var series = nameof(InvoiceSeries.TaxInvoice);
        await holder.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM invoice_number_counters WHERE seller_id = {seller} AND series = {series} FOR UPDATE");

        await using var waiter = fixture.CreateContext();
        await using var waiting = await waiter.Database.BeginTransactionAsync();
        var waiterStarted = await DbClockAsync(waiter); // the waiter's transaction is open before the lock is free (now() = its start)
        var take = Allocator(waiter).TakeAsync(seller, InvoiceSeries.TaxInvoice);
        await Task.Delay(700);
        take.IsCompleted.Should().BeFalse("the second issuer waits for the counter row");

        (await Allocator(holder).TakeAsync(seller, InvoiceSeries.TaxInvoice)).Serial.Should().Be(2);
        var released = await DbClockAsync(holder);
        await holding.CommitAsync();
        var taken = await take;
        await waiting.CommitAsync();

        taken.Serial.Should().Be(3);
        taken.IssuedAtUtc.Should().BeOnOrAfter(released, "the instant is read after the lock was granted, not when the issuer started waiting");
        (released - waiterStarted).Should().BeGreaterThan(TimeSpan.FromMilliseconds(500));
    }

    [PostgresFact]
    public async Task SerialsAndIssueTimes_RiseTogether_UnderParallelIssues()
    {
        var seller = await SellerAsync();
        var taken = await InParallelAsync(60, _ => TakeAsync(seller));
        var bySerial = taken.OrderBy(t => t.Serial).ToList();
        bySerial.Select(t => t.Serial).Should().Equal(Enumerable.Range(1, 60).Select(i => (long)i));
        bySerial.Select(t => t.IssuedAtUtc).Should().BeInAscendingOrder("serial order is time order within a series");
    }

    [PostgresFact]
    public async Task ADatabaseClockBehindTheLastIssue_RefusesToIssue()
    {
        await InstallTestClockAsync();
        var seller = await SellerAsync();
        var last = Utc("2029-07-01T00:00:00"); // 1 July 06:00 Dhaka, fiscal year 2029
        await TakeAsync(seller, at: last);

        await FluentActions.Invoking(() => TakeAsync(seller, at: last.AddSeconds(-1)))
            .Should().ThrowAsync<InvoiceClockBehindException>("the clock went back within the year");
        await FluentActions.Invoking(() => TakeAsync(seller, at: Utc("2029-06-30T17:00:00")))
            .Should().ThrowAsync<InvoiceClockBehindException>("the clock went back into the previous fiscal year");
        (await CounterAsync(seller)).Should().Be((2029, 2L, (DateTime?)last));
        (await TakeAsync(seller, at: last.AddSeconds(1))).Serial.Should().Be(2, "a refused issue took nothing");
    }

    [PostgresFact]
    public async Task AnAppServerClockOffByAnHour_ChangesNothing()
    {
        var seller = await SellerAsync();
        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(new ShiftedClock(TimeSpan.FromHours(1)))
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddScoped(_ => fixture.CreateContext())
            .AddScoped<IInvoiceNumberAllocator, InvoiceNumberAllocator>()
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MechwareDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();
        var before = await DbClockAsync(db);
        var taken = await scope.ServiceProvider.GetRequiredService<IInvoiceNumberAllocator>().TakeAsync(seller, InvoiceSeries.TaxInvoice);
        var after = await DbClockAsync(db);
        await tx.CommitAsync();

        taken.IssuedAtUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(after, "the instant is the database's, not the app server's (+1 h)");
        taken.FiscalYear.Should().Be(FiscalYear.Of(before));
    }

    private sealed class ShiftedClock(TimeSpan shift) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + shift;
    }

    // ---- Migration AddInvoiceNumbering: Up, Down, Up ------------------------------------------------------------------------

    [PostgresFact]
    public async Task Migration_AddInvoiceNumbering_GoesUpDownAndUpAgain()
    {
        var name = "mw_e62_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(TestInfra.AdminConnection))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync();
        }
        var cs = MechwareDatabase.WithDefaults(new NpgsqlConnectionStringBuilder(TestInfra.AdminConnection) { Database = name }.ConnectionString);
        try
        {
            (await MechwareDatabase.MigrateAsync(cs, TextWriter.Null, TextWriter.Null)).Should().Be(0);
            await using var ctx = new MechwareDbContext(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(cs).Options);
            (await ObjectsAsync(ctx)).Should().Be((1, 2, 1), "Up: table, guard + truncate triggers, guard function");

            await ctx.GetService<IMigrator>().MigrateAsync(LastMigrationBefore);
            (await ObjectsAsync(ctx)).Should().Be((0, 0, 0), "Down removes the table, the triggers and the function");

            (await MechwareDatabase.MigrateAsync(cs, TextWriter.Null, TextWriter.Null)).Should().Be(0);
            (await ObjectsAsync(ctx)).Should().Be((1, 2, 1), "Up again");
            await ctx.Database.ExecuteSqlRawAsync(InvoiceNumberingSql.Install); // a re-run changes nothing
            (await ObjectsAsync(ctx)).Should().Be((1, 2, 1));
            Pg(await Assert.ThrowsAnyAsync<Exception>(() => ctx.Database.ExecuteSqlRawAsync("TRUNCATE invoice_number_counters"))).SqlState
                .Should().Be(PostgresErrorCodes.IntegrityConstraintViolation, "the migrated guard is live");
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

    private static async Task<(int Tables, int Triggers, int Functions)> ObjectsAsync(MechwareDbContext ctx)
    {
        var row = (await ctx.Database.SqlQuery<int>($"""
            SELECT ((SELECT count(*) FROM pg_tables WHERE tablename = 'invoice_number_counters') * 100
                  + (SELECT count(*) FROM pg_trigger WHERE tgname LIKE 'trg_invoice_number_counters%') * 10
                  + (SELECT count(*) FROM pg_proc WHERE proname = 'invoice_number_counters_guard'))::int AS "Value"
            """).ToListAsync()).Single();
        return (row / 100, row / 10 % 10, row % 10);
    }
}
