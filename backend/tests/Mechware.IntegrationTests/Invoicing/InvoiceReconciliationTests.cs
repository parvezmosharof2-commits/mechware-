using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Mechware.Application.Identity;
using Mechware.Application.Invoicing;
using Mechware.Application.Orders;
using Mechware.Domain.Invoicing;
using Mechware.Domain.Orders;
using Mechware.Infrastructure.Invoicing;
using Mechware.Infrastructure.Orders;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;
using R = Mechware.Infrastructure.Invoicing.InvoiceReconciliationService;

namespace Mechware.IntegrationTests.Invoicing;

/// <summary>Shared steps of the E6.3d tests: real ships through <see cref="OrderFulfilmentService"/>, damage by raw SQL with every trigger off.</summary>
public abstract class InvoiceReconciliationTestBase(PostgresDatabaseFixture fixture)
{
    protected PostgresDatabaseFixture Fixture { get; } = fixture;

    private sealed class Staff(Guid id) : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => "203.0.113.9";
        public string? UserAgent => null;
        public string? CorrelationId => "corr-e63d";
        public bool HasPermission(string permission) => true;
    }

    /// <summary>Collects every log line, so a test can see the ALERT.</summary>
    protected sealed class ListLogger : ILogger<R>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add($"{logLevel}: {formatter(state, exception)}");
        }
    }

    protected sealed record World(Guid Seller, Guid Staff, Guid Buyer);

    protected static readonly InvoicingOptions LiveHost = InvoiceTestIssues.Options(Guid.NewGuid());
    protected static readonly InvoicingOptions OffHost = LiveHost with { Mode = InvoicingMode.Off };

    protected async Task<World> WorldAsync()
    {
        await using var db = Fixture.CreateContext();
        var seller = await InvoiceTestIssues.SellerAsync(db);
        var buyer = await db.Sellers.Where(s => s.Id == seller).Select(s => s.UserId).SingleAsync();
        return new World(seller, await InvoiceTestIssues.UserAsync(db), buyer);
    }

    /// <summary>A packed order of the seller shipped through the real ship transaction in <paramref name="mode"/>.</summary>
    protected async Task<Guid> ShipAsync(World w, InvoicingMode mode = InvoicingMode.Live, bool company = false, long shippingMinor = 12_000)
    {
        await using var db = Fixture.CreateContext();
        var buyer = company ? await InvoiceTestIssues.ActiveCompanyAsync(db) : null;
        var order = await InvoiceTestIssues.PackedOrderAsync(db, w.Buyer, [w.Seller], buyer, shippingMinor);
        var version = await db.Orders.Where(o => o.Id == order.Id).Select(o => EF.Property<uint>(o, "xmin")).SingleAsync();
        db.ChangeTracker.Clear();
        var user = new Staff(w.Staff);
        var options = InvoiceTestIssues.Options(w.Seller, mode);
        var issuer = new InvoiceIssuer(db, new InvoiceNumberAllocator(db, NullLogger<InvoiceNumberAllocator>.Instance), options, user, TimeProvider.System,
            NullLogger<InvoiceIssuer>.Instance);
        await new OrderFulfilmentService(db, user, TimeProvider.System, issuer).ShipAsync(order.Id, new ShipOrderRequest(version, "Steadfast", $"SF-{Guid.NewGuid():N}"[..20], null));
        return order.Id;
    }

    protected async Task<(Guid Invoice, Guid SubOrder, string Number)> InvoiceOfAsync(Guid order)
    {
        await using var db = Fixture.CreateContext();
        var i = await db.Invoices.Where(x => x.OrderId == order).Select(x => new { x.Id, x.SubOrderId, x.Number }).SingleAsync();
        return (i.Id, i.SubOrderId, i.Number);
    }

    protected async Task<Guid> SubOrderOfAsync(Guid order)
    {
        await using var db = Fixture.CreateContext();
        return await db.SubOrders.Where(s => s.OrderId == order).Select(s => s.Id).SingleAsync();
    }

    /// <summary>Damage the guards would refuse: one transaction with every trigger (guards, COMMIT checks, foreign keys) switched off.</summary>
    protected async Task RawAsync(string sql, params object[] args)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var off = new NpgsqlCommand("SET LOCAL session_replication_role = replica", conn, tx)) await off.ExecuteNonQueryAsync();
        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
        {
            for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("p" + i, args[i]);
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    /// <summary>An md5 of every row the pass reads (invoices, lines, counters, sub-orders, shipments, orders): a repair would change it.</summary>
    protected async Task<string> FingerprintAsync()
    {
        await using var db = Fixture.CreateContext();
        var parts = new List<string>();
        foreach (var table in new[] { "invoices", "invoice_lines", "invoice_number_counters", "sub_orders", "shipments", "orders" })
        {
            var sql = "SELECT coalesce(md5(string_agg(t::text, '|' ORDER BY t::text)), '') AS \"Value\" FROM " + table + " t";   // fixed table names only
            parts.Add((await db.Database.SqlQueryRaw<string>(sql).ToListAsync()).Single());
        }
        return string.Join(",", parts);
    }

    protected async Task<InvoiceReconciliationReport> ReconcileAsync(InvoicingOptions host, DateTime? now = null, ListLogger? log = null)
    {
        await using var db = Fixture.CreateContext();
        return await new R(db, host, (ILogger<R>?)log ?? NullLogger<R>.Instance).RunAsync(now ?? DateTime.UtcNow);
    }

    protected async Task<List<(Guid Id, Guid? Actor, string Details)>> AuditsOfAsync(Guid entityId)
    {
        await using var db = Fixture.CreateContext();
        return (await db.AuditLogs.AsNoTracking().Where(a => a.Action == R.FindingEvent && a.EntityId == entityId.ToString())
            .Select(a => new { a.Id, a.ActorId, a.NewValuesJson }).ToListAsync()).Select(a => (a.Id, a.ActorId, a.NewValuesJson ?? "")).ToList();
    }

    protected async Task<List<Guid>> OutboxOfAsync(Guid entityId)
    {
        await using var db = Fixture.CreateContext();
        return await db.OutboxMessages.AsNoTracking().Where(m => m.EventType == R.FindingEvent && m.AggregateId == entityId).Select(m => m.Id).ToListAsync();
    }
}

/// <summary>
/// E6.3d, threat model §১৪ IT13 + IT16, ADR-0012 §8, §17: damage seeded in this class's own throw-away database (mw_test_*) with every
/// trigger off (<c>session_replication_role = replica</c>, the residual "superuser" risk of §8); the pass names it, records it once a day
/// (audit + outbox, system actor), logs an ALERT and changes nothing. The whole database is checked, so each test asserts on its own rows.
/// </summary>
public sealed class InvoiceReconciliationDamageTests(PostgresDatabaseFixture fixture)
    : InvoiceReconciliationTestBase(fixture), IClassFixture<PostgresDatabaseFixture>
{
    [PostgresFact]
    public async Task Reconciliation_FindsAShippedOrderWithoutAnInvoice_AndAlerts()
    {
        var w = await WorldAsync();
        await ShipAsync(w);   // a Live 6.3: invoicing is live from here on
        Guid bypassed;
        await using (var db = Fixture.CreateContext()) bypassed = (await InvoiceTestIssues.ShippedOrderAsync(db, w.Seller)).Order.Id;   // shipped around the issuer
        var sub = await SubOrderOfAsync(bypassed);
        var log = new ListLogger();

        var report = await ReconcileAsync(LiveHost, log: log);

        report.Ran.Should().BeTrue();
        report.Findings.Where(f => f.EntityId == sub || f.EntityId == w.Seller).Should().ContainSingle()
            .Which.Should().Be(new InvoiceReconciliationFinding(R.MissingInvoice, "sub_orders", sub, $"order {bypassed:D}", 1, 0));
        var audit = (await AuditsOfAsync(sub)).Should().ContainSingle().Subject;
        audit.Actor.Should().BeNull("the system records it");
        audit.Details.Should().Contain(R.MissingInvoice);
        (await OutboxOfAsync(sub)).Should().Equal(audit.Id);
        log.Lines.Should().Contain(l => l.StartsWith("Error: ALERT invoice reconciliation: missing_invoice", StringComparison.Ordinal) && l.Contains(sub.ToString()));
    }

    [PostgresFact]
    public async Task Reconciliation_FindsAContentHashMismatchOrAGap_AndAlerts_WithoutRepairing()
    {
        var w = await WorldAsync();
        var orders = new List<Guid>();
        for (var i = 0; i < 3; i++) orders.Add(await ShipAsync(w, company: i == 1));
        var (tampered, _, _) = await InvoiceOfAsync(orders[0]);
        var (deleted, _, number) = await InvoiceOfAsync(orders[1]);
        var (untouched, _, _) = await InvoiceOfAsync(orders[2]);
        await RawAsync("UPDATE invoice_lines SET description_en = 'PLC controller (refurbished)' WHERE invoice_id = @p0 AND line_no = 1", tampered);
        await RawAsync("DELETE FROM invoice_lines WHERE invoice_id = @p0; DELETE FROM invoices WHERE id = @p0", deleted);   // serial 2 of 3
        var before = await FingerprintAsync();
        var log = new ListLogger();

        var report = await ReconcileAsync(LiveHost, log: log);

        var year = number.Split('-')[2] + "-" + number.Split('-')[3];
        var mine = report.Findings.Where(f => f.EntityId == tampered || f.EntityId == untouched || f.EntityId == w.Seller).ToList();
        mine.Should().BeEquivalentTo(new[]
        {
            new InvoiceReconciliationFinding(R.ContentHash, "invoices", tampered, (await InvoiceNumberAsync(tampered))!, 0, 1),
            new InvoiceReconciliationFinding(R.NumberGap, "sellers", w.Seller, $"TaxInvoice {year}", 3, 2)
        });
        report.Findings.Should().Contain(f => f.Check == R.MissingInvoice && f.Detail == $"order {orders[1]:D}");
        (await FingerprintAsync()).Should().Be(before, "the pass never repairs: every invoice, line, counter, sub-order and shipment is as it was");
        (await AuditsOfAsync(tampered)).Should().ContainSingle();
        (await AuditsOfAsync(w.Seller)).Should().ContainSingle();
        log.Lines.Should().Contain(l => l.Contains("ALERT invoice reconciliation: content_hash") && l.Contains(tampered.ToString()));
    }

    private async Task<string?> InvoiceNumberAsync(Guid invoice)
    {
        await using var db = Fixture.CreateContext();
        return await db.Invoices.Where(i => i.Id == invoice).Select(i => i.Number).SingleOrDefaultAsync();
    }

    [PostgresFact]
    public async Task AGap_IsFound_InTheFirstFiscalYearOfASeries_AndAtSerialOne()
    {
        // Seller A: serials 1-3 in year Y; serial 3 moved to Y+1 as its serial 1 (counter rolled over), serial 1 of Y deleted → Y = {2}.
        var a = await WorldAsync();
        var aOrders = new List<Guid>();
        for (var i = 0; i < 3; i++) aOrders.Add(await ShipAsync(a));
        var (aFirst, _, number) = await InvoiceOfAsync(aOrders[0]);
        var (aMoved, _, _) = await InvoiceOfAsync(aOrders[2]);
        var y = int.Parse(number.Split('-')[2], System.Globalization.CultureInfo.InvariantCulture);
        await RawAsync("""
            UPDATE invoices SET fiscal_year = fiscal_year + 1, serial = 1,
                number = split_part(number, '-', 1) || '-INV-' || (fiscal_year + 1)::text || '-' || lpad(((fiscal_year + 2) % 100)::text, 2, '0') || '-000001'
            WHERE id = @p0;
            UPDATE invoice_number_counters SET fiscal_year = fiscal_year + 1, next_serial = 2 WHERE seller_id = @p1 AND series = 'TaxInvoice';
            DELETE FROM invoice_lines WHERE invoice_id = @p2; DELETE FROM invoices WHERE id = @p2;
            """, aMoved, a.Seller, aFirst);
        // Seller B: serials 1-2 in the counter's own year; serial 1 deleted → {2}, the counter (next 3) still agrees.
        var b = await WorldAsync();
        var bFirst = await ShipAsync(b);
        await ShipAsync(b);
        var (bDeleted, _, _) = await InvoiceOfAsync(bFirst);
        await RawAsync("DELETE FROM invoice_lines WHERE invoice_id = @p0; DELETE FROM invoices WHERE id = @p0", bDeleted);

        var report = await ReconcileAsync(LiveHost);

        report.Findings.Where(f => f.EntityId == a.Seller).Should().Equal(
            new InvoiceReconciliationFinding(R.NumberGap, "sellers", a.Seller, $"TaxInvoice {FiscalYear.Label(y)}", 2, 1));
        report.Findings.Where(f => f.EntityId == b.Seller).Should().Equal(
            new InvoiceReconciliationFinding(R.NumberGap, "sellers", b.Seller, $"TaxInvoice {FiscalYear.Label(y)}", 2, 1));
    }

    [PostgresFact]
    public async Task AnInvoiceWhoseTotalDiffersFromItsSubOrder_IsFound_AndNothingIsRepaired()
    {
        var w = await WorldAsync();
        var order = await ShipAsync(w, company: true);
        var (invoice, sub, number) = await InvoiceOfAsync(order);
        long total;
        await using (var db = Fixture.CreateContext()) total = await db.Invoices.Where(i => i.Id == invoice).Select(i => i.TotalMinor).SingleAsync();
        await RawAsync("UPDATE sub_orders SET total = total + 100 WHERE id = @p0", sub);
        var before = await FingerprintAsync();

        var report = await ReconcileAsync(LiveHost);

        report.Findings.Where(f => f.EntityId == invoice || f.EntityId == sub || f.EntityId == w.Seller).Should().Equal(
            new InvoiceReconciliationFinding(R.InvoiceTotal, "invoices", invoice, number, total + 100, total));
        (await FingerprintAsync()).Should().Be(before);
        (await AuditsOfAsync(invoice)).Should().ContainSingle();
    }

    [PostgresFact]
    public async Task OnAHostThatRunsOff_ShippedOrdersAreNotCheckedForAnInvoice_TheOtherChecksStillRun()
    {
        var w = await WorldAsync();
        var order = await ShipAsync(w);
        Guid bypassed;
        await using (var db = Fixture.CreateContext()) bypassed = (await InvoiceTestIssues.ShippedOrderAsync(db, w.Seller)).Order.Id;
        var sub = await SubOrderOfAsync(bypassed);
        var (invoice, _, _) = await InvoiceOfAsync(order);
        await RawAsync("UPDATE invoices SET purchase_order_number = 'PO-EDITED' WHERE id = @p0", invoice);

        var report = await ReconcileAsync(OffHost);

        report.Findings.Should().NotContain(f => f.Check == R.MissingInvoice || f.Check == R.DuplicateInvoice);
        report.Findings.Should().Contain(f => f.Check == R.ContentHash && f.EntityId == invoice);
        (await ReconcileAsync(LiveHost)).Findings.Should().Contain(f => f.Check == R.MissingInvoice && f.EntityId == sub);
    }

    [PostgresFact]
    public async Task OnePassAtATime_APassWhileAnotherHoldsTheLockSkips_AndParallelPassesRecordAFindingOnceADay()
    {
        var w = await WorldAsync();
        var (invoice, _, _) = await InvoiceOfAsync(await ShipAsync(w));
        await RawAsync("UPDATE invoice_lines SET quantity = quantity + 1 WHERE invoice_id = @p0 AND line_no = 1", invoice);
        var day = DateTime.UtcNow.AddDays(3);

        await using (var conn = new NpgsqlConnection(Fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using (var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('invoice-reconciliation', 0))", conn, tx))
                await hold.ExecuteNonQueryAsync();
            var skipped = await ReconcileAsync(LiveHost, day);
            (skipped.Ran, skipped.Findings.Count, skipped.NewlyRecorded).Should().Be((false, 0, 0));
            (await AuditsOfAsync(invoice)).Should().BeEmpty();
            await tx.CommitAsync();
        }

        var passes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => ReconcileAsync(LiveHost, day))));
        passes.Should().Contain(p => p.Ran && p.Findings.Any(f => f.EntityId == invoice));
        (await AuditsOfAsync(invoice)).Should().ContainSingle("the same finding is recorded once a day, whatever the passes");
        (await OutboxOfAsync(invoice)).Should().ContainSingle();
        (await ReconcileAsync(LiveHost, day)).NewlyRecorded.Should().Be(0);
        (await ReconcileAsync(LiveHost, day.AddDays(1))).Findings.Should().Contain(f => f.EntityId == invoice);
        (await AuditsOfAsync(invoice)).Should().HaveCount(2, "the next night records it again while the damage lasts");
    }
}

/// <summary>E6.3d: a database written only by the real ship paths, in every mode, gives no finding (own throw-away database).</summary>
public sealed class InvoiceReconciliationCleanTests(PostgresDatabaseFixture fixture)
    : InvoiceReconciliationTestBase(fixture), IClassFixture<PostgresDatabaseFixture>
{
    [PostgresFact]
    public async Task ACleanDatabase_GivesNoFinding_AndOrdersShippedInOffOrSpecimenModeAreIgnored()
    {
        var a = await WorldAsync();
        var b = await WorldAsync();
        var off = await ShipAsync(a, InvoicingMode.Off);          // before invoicing went live: no 6.3, by design
        await ShipAsync(a, InvoicingMode.Specimen);               // a SPEC number, not a live one
        await ShipAsync(a);
        await ShipAsync(a, company: true);
        await ShipAsync(a, shippingMinor: 0);
        await ShipAsync(b, company: true);
        await ShipAsync(b);

        var report = await ReconcileAsync(LiveHost);

        (report.Ran, report.InvoicesChecked, report.NewlyRecorded).Should().Be((true, 6, 0));
        report.Findings.Should().BeEmpty();
        (await AuditsOfAsync(await SubOrderOfAsync(off))).Should().BeEmpty();
        (await ReconcileAsync(OffHost)).Findings.Should().BeEmpty();
        await using var db = Fixture.CreateContext();
        (await db.AuditLogs.CountAsync(x => x.Action == R.FindingEvent)).Should().Be(0);
        (await db.OutboxMessages.CountAsync(x => x.EventType == R.FindingEvent)).Should().Be(0);
    }
}
