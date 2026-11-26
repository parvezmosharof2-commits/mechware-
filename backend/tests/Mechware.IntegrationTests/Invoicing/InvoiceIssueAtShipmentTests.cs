using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Mechware.Application.Identity;
using Mechware.Application.Orders;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Invoicing;
using Mechware.Domain.Orders;
using Mechware.Infrastructure.Credit;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Invoicing;
using Mechware.Infrastructure.Orders;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Invoicing;

/// <summary>
/// E6.3c, threat model §১৪ IT7, IT9, IT13, IT15: the Mushak-6.3 is issued inside the ship transaction (ADR-0012 §2, §15). Off issues
/// nothing, Specimen takes SPEC numbers, Live takes real ones or refuses with 409 and then nothing of the ship stays except the
/// refusal's audit row. The counter is the last lock: while another transaction holds it, the shipping one already holds the order,
/// the company and the seller. Each test uses its own seller, so the series never meet.
/// </summary>
[Collection(ExclusiveTimingCollection.Name)]
public sealed class InvoiceIssueAtShipmentTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private sealed class Staff(Guid id) : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => "203.0.113.9";
        public string? UserAgent => null;
        public string? CorrelationId => "corr-e63c";
        public bool HasPermission(string permission) => true;
    }

    private sealed record World(Guid Seller, Guid Staff, Guid Buyer);

    private async Task<World> WorldAsync()
    {
        await using var db = fixture.CreateContext();
        var seller = await InvoiceTestIssues.SellerAsync(db);
        var buyer = await db.Sellers.Where(s => s.Id == seller).Select(s => s.UserId).SingleAsync();
        return new World(seller, await InvoiceTestIssues.UserAsync(db), buyer);
    }

    private async Task<Guid> PackedAsync(World w, Company? company = null)
    {
        await using var db = fixture.CreateContext();
        return (await InvoiceTestIssues.PackedOrderAsync(db, w.Buyer, [w.Seller], company)).Id;
    }

    private async Task<Company> CompanyAsync(bool active = true, string? bin = null)
    {
        await using var db = fixture.CreateContext();
        var digits = string.Concat(Guid.NewGuid().ToByteArray().Select(b => (char)('0' + b % 10)))[..13];   // BINs are unique
        var company = Company.Register(Guid.NewGuid(), "Square Textiles Ltd", $"TL-{Guid.NewGuid():N}"[..12], digits, null, null);
        if (active) company.Activate();
        if (bin is not null) typeof(Company).GetProperty(nameof(Company.Bin))!.SetValue(company, bin);   // a BIN the register rules would refuse today
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company;
    }

    private static OrderFulfilmentService Fulfilment(MechwareDbContext db, Guid staff, InvoicingOptions options)
    {
        var user = new Staff(staff);
        var issuer = new InvoiceIssuer(db, new InvoiceNumberAllocator(db, NullLogger<InvoiceNumberAllocator>.Instance), options, user, TimeProvider.System,
            NullLogger<InvoiceIssuer>.Instance);
        return new OrderFulfilmentService(db, user, TimeProvider.System, issuer);
    }

    private async Task<uint> VersionOf(Guid order)
    {
        await using var db = fixture.CreateContext();
        return await db.Orders.Where(o => o.Id == order).Select(o => EF.Property<uint>(o, "xmin")).SingleAsync();
    }

    private async Task<OrderFulfilmentDto> ShipAsync(World w, InvoicingOptions options, Guid order, uint? version = null)
    {
        var v = version ?? await VersionOf(order);
        await using var db = fixture.CreateContext();
        return await Fulfilment(db, w.Staff, options).ShipAsync(order, new ShipOrderRequest(v, "Steadfast", $"SF-{Guid.NewGuid():N}"[..20], null));
    }

    private sealed record State(OrderStatus Status, int Shipments, List<Invoice> Invoices, (long Next, DateTime? Last)? Counter, List<string> Audits,
        List<string> Events);

    private async Task<State> StateAsync(World w, Guid order, InvoiceSeries series = InvoiceSeries.TaxInvoice)
    {
        await using var db = fixture.CreateContext();
        var counter = await db.InvoiceNumberCounters.AsNoTracking().Where(c => c.SellerId == w.Seller && c.Series == series)
            .Select(c => new { c.NextSerial, c.LastIssuedAtUtc }).SingleOrDefaultAsync();
        var invoices = await db.Invoices.AsNoTracking().Include(i => i.Lines).Where(i => i.OrderId == order).ToListAsync();
        var ids = invoices.Select(i => i.Id).ToList();
        var ids2 = ids.Select(i => i.ToString()).Append(order.ToString()).ToList();
        return new State(await db.Orders.Where(o => o.Id == order).Select(o => o.Status).SingleAsync(),
            await db.Set<Shipment>().CountAsync(s => s.OrderId == order), invoices,
            counter is null ? null : (counter.NextSerial, counter.LastIssuedAtUtc),
            await db.AuditLogs.Where(a => ids2.Contains(a.EntityId)).OrderBy(a => a.CreatedAtUtc).Select(a => a.Action).ToListAsync(),
            await db.OutboxMessages.Where(m => m.AggregateId == order || ids.Contains(m.AggregateId)).Select(m => m.EventType).ToListAsync());
    }

    private static InvoicingOptions Live(World w) => InvoiceTestIssues.Options(w.Seller);

    // ---- modes ----------------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Off_ShipsWithoutAnInvoice_AndTouchesNoCounter()
    {
        var w = await WorldAsync();
        var order = await PackedAsync(w);
        var dto = await ShipAsync(w, Live(w) with { Mode = InvoicingMode.Off }, order);

        (dto.Status, dto.InvoiceNumbers.Count).Should().Be(("shipped", 0));
        var s = await StateAsync(w, order);
        (s.Status, s.Shipments, s.Invoices.Count, s.Counter).Should().Be((OrderStatus.Shipped, 1, 0, ((long, DateTime?)?)null));
        s.Audits.Should().Equal("order.shipped");
    }

    [PostgresFact]
    public async Task Specimen_IssuesASpecNumber_OfItsOwnSeries()
    {
        var w = await WorldAsync();
        var order = await PackedAsync(w);
        var dto = await ShipAsync(w, Live(w) with { Mode = InvoicingMode.Specimen }, order);

        var s = await StateAsync(w, order, InvoiceSeries.SpecimenTaxInvoice);
        var invoice = s.Invoices.Single();
        dto.InvoiceNumbers.Should().Equal(InvoiceNumber.Format("XX", InvoiceSeries.SpecimenTaxInvoice, invoice.FiscalYear, 1));
        invoice.Number.Should().StartWith("SPEC-INV-");
        (invoice.Mode, invoice.Series, s.Counter!.Value.Next).Should().Be((InvoicingMode.Specimen, InvoiceSeries.SpecimenTaxInvoice, 2));
        (await StateAsync(w, order)).Counter.Should().BeNull("the live series is untouched");
    }

    [PostgresFact]
    public async Task Live_IssuesARealNumber_TheInvoiceAndTheCounterCommitTogether_WithAuditAndOutbox()
    {
        var w = await WorldAsync();
        var company = await CompanyAsync();
        var order = await PackedAsync(w, company);
        var dto = await ShipAsync(w, Live(w), order);

        var s = await StateAsync(w, order);
        var invoice = s.Invoices.Single();
        dto.InvoiceNumbers.Should().Equal(InvoiceNumber.Format(InvoiceTestIssues.Prefix(w.Seller), InvoiceSeries.TaxInvoice, invoice.FiscalYear, 1));
        (invoice.Mode, invoice.Kind, invoice.Serial, invoice.CompanyId, invoice.IssuedBy).Should().Be((InvoicingMode.Live, InvoiceKind.TaxInvoice, 1L, company.Id, w.Staff));
        // The issue instant is the database instant of the counter step (the COMMIT check compares them), not the app clock of the ship.
        s.Counter.Should().Be((2L, (DateTime?)invoice.IssuedAtUtc));
        InvoiceContentHash.Matches(invoice, invoice.ContentHash).Should().BeTrue();
        invoice.Buyer.Bin.Should().Be(company.Bin);

        await using var db = fixture.CreateContext();
        var sub = await db.Set<SubOrder>().AsNoTracking().SingleAsync(x => x.OrderId == order);
        var shipment = await db.Set<Shipment>().AsNoTracking().SingleAsync(x => x.OrderId == order);
        (invoice.SubOrderId, invoice.ShipmentId, invoice.NetMinor + invoice.SdMinor + invoice.VatMinor, invoice.TotalMinor)
            .Should().Be((sub.Id, (Guid?)shipment.Id, sub.TotalMinor, sub.TotalMinor), "IT8 on a real order: the invoice is the sub-order to the poisha");
        invoice.VatMinor.Should().Be(sub.TaxMinor + invoice.Lines.Where(l => l.Kind == InvoiceLineKind.Delivery).Sum(l => l.VatMinor));
        s.Audits.Should().Equal("order.shipped", "invoice.issued");
        s.Events.Should().Contain([nameof(OrderShippedEvent), InvoiceIssuer.IssuedEvent]);
        var audit = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.EntityId == invoice.Id.ToString());
        (audit.ActorId, audit.CreatedAtUtc).Should().Be(((Guid?)w.Staff, invoice.IssuedAtUtc));
        audit.NewValuesJson.Should().Contain(invoice.Number).And.Contain(invoice.ContentHash);

        var staffView = await new StaffOrderReadService(db).GetAsync(order, DateTime.UtcNow);
        staffView.InvoiceNumbers.Should().Equal(invoice.Number);
    }

    [PostgresFact]
    public async Task AnOrderGetsExactlyOneInvoice_ShippingTwice_IssuesOnce()
    {
        var w = await WorldAsync();
        var order = await PackedAsync(w);
        var first = await ShipAsync(w, Live(w), order);
        var again = () => ShipAsync(w, Live(w), order, first.RowVersion);
        (await again.Should().ThrowAsync<ConflictException>()).Which.Code.Should().BeOneOf("ORDER_ALREADY_SHIPPED", "ORDER_INVALID_TRANSITION");
        var s = await StateAsync(w, order);
        (s.Invoices.Count, s.Counter!.Value.Next, s.Shipments).Should().Be((1, 2L, 1));
    }

    // ---- refusals -------------------------------------------------------------------------------------------------------------

    [PostgresTheory]
    [InlineData("setup", "INVOICE_SETUP_INCOMPLETE")]
    [InlineData("seller", "INVOICE_SELLER_NOT_SUPPORTED")]
    [InlineData("inactive", "INVOICE_BUYER_BIN_INVALID")]
    [InlineData("bad-bin", "INVOICE_BUYER_BIN_INVALID")]
    public async Task ALiveRefusal_LeavesNoShipmentNoInvoiceNoCounterStep_AndIsAudited(string why, string code)
    {
        var w = await WorldAsync();
        var company = why switch { "inactive" => await CompanyAsync(active: false), "bad-bin" => await CompanyAsync(bin: "12345"), _ => await CompanyAsync() };
        var options = why switch
        {
            "setup" => Live(w) with { LegalNameBn = null },
            "seller" => Live(w) with { IssuerSellerId = Guid.NewGuid() },
            _ => Live(w)
        };
        var order = await PackedAsync(w, company);
        var before = await VersionOf(order);

        var ship = () => ShipAsync(w, options, order);
        (await ship.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be(code);

        var s = await StateAsync(w, order);
        (s.Status, s.Shipments, s.Invoices.Count, s.Counter).Should().Be((OrderStatus.Packed, 0, 0, ((long, DateTime?)?)null), why);
        (await VersionOf(order)).Should().Be(before, "the order row was not written");
        s.Audits.Should().Equal("invoice.issue_refused");
        s.Events.Should().NotContain(nameof(OrderShippedEvent)).And.NotContain(InvoiceIssuer.IssuedEvent);
        await using var db = fixture.CreateContext();
        var refusal = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.EntityId == order.ToString());
        (refusal.ActorId, refusal.EntityName, refusal.CorrelationId).Should().Be(((Guid?)w.Staff, "orders", "corr-e63c"));
        JsonDocument.Parse(refusal.NewValuesJson!).RootElement.GetProperty("code").GetString().Should().Be(code);

        // Fixed, the same order ships and takes serial 1: the refusal used no number.
        var fixedCompany = why is "inactive" or "bad-bin";
        if (!fixedCompany)
            (await ShipAsync(w, Live(w), order)).InvoiceNumbers.Single().Should().EndWith("-000001");
    }

    // ---- concurrency ----------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task ParallelIssuesGetConsecutiveNumbers_WithNoGaps()
    {
        var w = await WorldAsync();
        var orders = new List<Guid>();
        for (var i = 0; i < 200; i++) orders.Add(await PackedAsync(w));
        var versions = new Dictionary<Guid, uint>();
        foreach (var o in orders) versions[o] = await VersionOf(o);

        using var gate = new SemaphoreSlim(40);
        var results = await Task.WhenAll(orders.Select(async o =>
        {
            await gate.WaitAsync();
            try { return await ShipAsync(w, Live(w), o, versions[o]); }
            finally { gate.Release(); }
        }));

        results.Should().OnlyContain(r => r.Status == "shipped" && r.InvoiceNumbers.Count == 1);
        await using var db = fixture.CreateContext();
        var issued = await db.Invoices.AsNoTracking().Where(i => i.SellerId == w.Seller).OrderBy(i => i.Serial)
            .Select(i => new { i.Serial, i.IssuedAtUtc, i.OrderId }).ToListAsync();
        issued.Select(i => i.Serial).Should().Equal(Enumerable.Range(1, 200).Select(i => (long)i));
        issued.Select(i => i.IssuedAtUtc).Should().BeInAscendingOrder("serial order is time order");
        issued.Select(i => i.OrderId).Should().OnlyHaveUniqueItems().And.BeEquivalentTo(orders);
        (await db.InvoiceNumberCounters.AsNoTracking().SingleAsync(c => c.SellerId == w.Seller && c.Series == InvoiceSeries.TaxInvoice)).NextSerial.Should().Be(201);
    }

    [PostgresFact]
    public async Task ShipCancelInParallel_NeverDeadlock_OneWinner_AndEveryNumberIsUsedOnce()
    {
        var w = await WorldAsync();
        long expectedNext = 1;
        for (var round = 0; round < 6; round++)
        {
            var order = await PackedAsync(w);
            var v = await VersionOf(order);
            var ship = Task.Run(async () => { try { await ShipAsync(w, Live(w), order, v); return (Exception?)null; } catch (Exception e) { return e; } });
            var cancel = Task.Run(async () =>
            {
                try
                {
                    await using var db = fixture.CreateContext();
                    var user = new Staff(w.Staff);
                    await new OrderCancellationService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System, user: user),
                        new CreditLedgerService(db, user, TimeProvider.System), user, TimeProvider.System)
                        .CancelConfirmedAsync(order, new CancelConfirmedOrderRequest("customer called: wrong part ordered"));
                    return (Exception?)null;
                }
                catch (Exception e) { return e; }
            });
            var (shipError, cancelError) = (await ship, await cancel);

            new[] { shipError, cancelError }.Count(e => e is null).Should().Be(1, $"round {round}: {shipError} / {cancelError}");
            new[] { shipError, cancelError }.Where(e => e is not null).Should().OnlyContain(e => e is ConflictException, "a 409, never a deadlock or a 500");
            var s = await StateAsync(w, order);
            if (shipError is null)
            {
                expectedNext++;
                (s.Status, s.Invoices.Count).Should().Be((OrderStatus.Shipped, 1));
            }
            else (s.Status, s.Invoices.Count, s.Shipments).Should().Be((OrderStatus.Cancelled, 0, 0));
            (s.Counter?.Next ?? 1).Should().Be(expectedNext, "a lost ship gives its number back");
        }
    }

    /// <summary>Holds the live counter of the seller in another transaction until <see cref="IAsyncDisposable.DisposeAsync"/> (rolled back).</summary>
    private async Task<MechwareDbContext> HoldCounterAsync(World w)
    {
        var db = fixture.CreateContext();
        await db.Database.BeginTransactionAsync();
        (await db.Database.SqlQuery<int>(
            $"SELECT 1 AS \"Value\" FROM invoice_number_counters WHERE seller_id = {w.Seller} AND series = 'TaxInvoice' FOR UPDATE").ToListAsync()).Should().Equal(1);
        return db;
    }

    private async Task WaitUntilSomeoneWaitsOnALockAsync()
    {
        await using var db = fixture.CreateContext();
        for (var i = 0; i < 200; i++)
        {
            var waiting = await db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'
                """).SingleAsync();
            if (waiting > 0) return;
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException("The ship never waited on the held counter.");
    }

    private async Task<string?> LockErrorAsync(FormattableString sql)
    {
        await using var db = fixture.CreateContext();
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(sql);
            return null;
        }
        catch (PostgresException e)
        {
            return e.SqlState;
        }
    }

    [PostgresFact]
    public async Task TheCounterIsAlwaysTheLastLock()
    {
        var w = await WorldAsync();
        var company = await CompanyAsync();
        await ShipAsync(w, Live(w), await PackedAsync(w, company));   // serial 1: the counter row exists
        var order = await PackedAsync(w, company);
        var v = await VersionOf(order);

        Task<OrderFulfilmentDto> ship;
        await using (var holder = await HoldCounterAsync(w))
        {
            ship = Task.Run(() => ShipAsync(w, Live(w), order, v));
            await WaitUntilSomeoneWaitsOnALockAsync();
            // While the ship waits for the counter it already holds every other row it needs (ADR-0012 §15: nothing after the counter).
            (await LockErrorAsync($"SELECT 1 FROM orders WHERE id = {order} FOR NO KEY UPDATE NOWAIT")).Should().Be(PostgresErrorCodes.LockNotAvailable, "the order");
            (await LockErrorAsync($"SELECT 1 FROM companies WHERE id = {company.Id} FOR UPDATE NOWAIT")).Should().Be(PostgresErrorCodes.LockNotAvailable, "the company");
            (await LockErrorAsync($"SELECT 1 FROM sellers WHERE id = {w.Seller} FOR UPDATE NOWAIT")).Should().Be(PostgresErrorCodes.LockNotAvailable, "the seller");
            ship.IsCompleted.Should().BeFalse();
        }

        (await ship).InvoiceNumbers.Single().Should().EndWith("-000002");
    }

    [PostgresFact]
    public async Task AStaleRowVersion_IsRefusedBeforeAnyNumberIsTaken()
    {
        var w = await WorldAsync();
        await ShipAsync(w, Live(w), await PackedAsync(w));
        var order = await PackedAsync(w);
        var stale = await VersionOf(order) - 1;

        await using (await HoldCounterAsync(w))
        {
            // With the counter held elsewhere, a ship that took a number first would wait here; this one answers at once.
            var ship = ShipAsync(w, Live(w), order, stale);
            var refused = async () => await ship.WaitAsync(TimeSpan.FromSeconds(10));
            (await refused.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("ORDER_CHANGED");
        }

        var s = await StateAsync(w, order);
        (s.Status, s.Shipments, s.Invoices.Count, s.Counter!.Value.Next).Should().Be((OrderStatus.Packed, 0, 0, 2L));
        s.Audits.Should().BeEmpty("a stale rowVersion is not an invoice refusal");
    }
}
