using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Approvals;
using Mechware.Application.Companies;
using Mechware.Application.Credit;
using Mechware.Application.Identity;
using Mechware.Application.Orders;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Domain.Orders;
using Mechware.Infrastructure.Approvals;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Credit;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Orders;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Outbox;
using Mechware.IntegrationTests.Orders;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Credit;

/// <summary>
/// B3.7, threat model §১২ CS25 (and CS16, CS29 for the hold) against a real database with every ledger guard installed: a company with a
/// charge more than <c>Credit:HoldAfterDaysPastDue</c> (15) days past due places no new credit order, at placement and at
/// approval-confirmation, decided under the company row lock; payments are never blocked; an Admin's lift lasts its days only; the
/// Worker sweep notes a hold once, with an audit row and an outbox event.
/// </summary>
public class CreditHoldServiceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string Ip = "203.0.113.37";
    private const long Unit = 100_000;            // the test variant's price: a company order of n units totals n × Unit
    private const long RuleFrom = 200_000;        // the approval rule covers orders from 2 units
    private static long _binSeq = 3_990_000_000;
    private static DateOnly Today => CreditCalendar.DhakaDate(DateTime.UtcNow);
    private static string Ref() => "TRX" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
    private static string Key() => "key-" + Guid.NewGuid().ToString("N");

    private sealed class Staff(Guid id, string correlationId) : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => Ip;
        public string? UserAgent => null;
        public string? CorrelationId => correlationId;
        public bool HasPermission(string permission) => true;
    }

    private sealed class Nobody : ICurrentUserService
    {
        public Guid? UserId => null;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => null;
        public string? UserAgent => null;
        public string? CorrelationId => null;
        public bool HasPermission(string permission) => false;
    }

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private MechwareDbContext Ctx() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(fixture.ConnectionString)
        .AddInterceptors(new DomainEventOutboxInterceptor()).Options);

    private sealed record Team(Guid Company, Guid Admin, IReadOnlyList<Guid> Buyers, Guid Approver)
    {
        public Guid Buyer => Buyers[0];
    }

    private async Task<Team> TeamAsync(int buyers = 1, bool approvalRule = false, long limitMinor = 100_000_000, bool activate = true)
    {
        var admin = await UserAsync();
        var approver = await UserAsync();
        var buyerIds = new List<Guid>();
        for (var i = 0; i < buyers; i++) buyerIds.Add(await UserAsync());
        await using var db = fixture.CreateContext();
        var companies = new CompanyService(db);
        var company = (await companies.RegisterAsync(admin, new RegisterCompanyRequest("Overdue Traders Ltd", "TL-B37-1",
            (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13"), null, null))).Id;
        foreach (var buyer in buyerIds)
            await companies.AddAcceptedMemberAsync(admin, company, new AddMemberRequest(buyer, CompanyRoles.Buyer, null, null));
        await companies.AddAcceptedMemberAsync(admin, company, new AddMemberRequest(approver, CompanyRoles.Approver, null, null));
        if (!activate) return new Team(company, admin, buyerIds, approver);
        await companies.AdminActivateAsync(company);
        await companies.AdminSetCreditAsync(company, new SetCreditRequest(CreditTerms.Net30, limitMinor));
        if (approvalRule)
            await new ApprovalService(db, TimeProvider.System).AddRuleAsync(admin, company, new ApprovalRuleRequest("From 2 units", RuleFrom, null, null, [approver]));
        return new Team(company, admin, buyerIds, approver);
    }

    private Task<Guid> UserAsync() => TestUsers.CreateAsync(fixture);

    private async Task<Guid> VariantAsync(int onHand = 50)
    {
        await using var db = fixture.CreateContext();
        return (await CatalogTestData.SeedVariantAsync(db, onHand, priceMinor: Unit)).VariantId;
    }

    /// <summary>A confirmed credit order charged <paramref name="daysAgo"/> days ago (Net-30: due 30 days after that).</summary>
    private static async Task<Guid> ChargeAsync(MechwareDbContext db, Team t, long amount, int daysAgo, bool locked = false)
    {
        var order = await CreditTestData.InsertOrderAsync(db, t.Admin, t.Company, amount);
        var company = locked
            ? (await db.Companies.FromSqlInterpolated($"SELECT c.*, c.xmin FROM companies c WHERE c.id = {t.Company} FOR UPDATE").ToListAsync()).Single()
            : await db.Companies.SingleAsync(c => c.Id == t.Company);
        var lines = await db.CreditLedgerEntries.Where(l => l.CompanyId == t.Company).ToListAsync();
        var at = DateTime.UtcNow.AddDays(-daysAgo);
        db.CreditLedgerEntries.Add(new CreditLedger(company, lines).Charge(order, amount, at, at).Entry);
        await db.SaveChangesAsync();
        return order;
    }

    private async Task<Guid> ChargedOrderAsync(Team t, long amount, int daysAgo)
    {
        await using var db = fixture.CreateContext();
        return await ChargeAsync(db, t, amount, daysAgo);
    }

    private OrderService NewOrders(MechwareDbContext db, DateTime? now, CreditOptions? options) =>
        new(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), now is null ? TimeProvider.System : new FixedClock(now.Value),
            ShippingTestKit.DevelopmentQuoter(db), new Infrastructure.Pricing.PriceListResolver(db), credit: options ?? new CreditOptions());

    private async Task<PlacedOrder> PlaceAsync(Guid buyer, Guid company, Guid variant, int qty = 1, bool onCredit = true, DateTime? now = null, CreditOptions? options = null)
    {
        await using var db = fixture.CreateContext();
        return await NewOrders(db, now, options).PlaceAsync(new PlaceOrderRequest(buyer, Key(), [new PlaceOrderLine(variant, qty)], null, company, PayOnCredit: onCredit));
    }

    private async Task<T> Pay<T>(Guid actor, Func<CreditPaymentService, Task<T>> action)
    {
        await using var db = Ctx();
        return await action(new CreditPaymentService(db, new Staff(actor, "corr-pay"), new CreditOptions(), TimeProvider.System));
    }

    private async Task<Guid> PaidAsync(Guid company, long amount)
    {
        var (recorder, verifier) = (await UserAsync(), await UserAsync());
        var payment = (await Pay(recorder, s => s.RecordAsync(company, new RecordCreditPaymentRequest("rtgs", amount, Today, Ref()), Key()))).Payment.Id;
        await Pay(verifier, s => s.VerifyAsync(payment, new VerifyCreditPaymentRequest(Today)));
        return payment;
    }

    private async Task<T> Holds<T>(Func<CreditHoldService, Task<T>> action, Guid? actor = null, DateTime? now = null, CreditOptions? options = null)
    {
        await using var db = Ctx();
        ICurrentUserService who = actor is { } a ? new Staff(a, "corr-hold") : new Nobody();
        return await action(new CreditHoldService(db, who, options ?? new CreditOptions(), now is null ? TimeProvider.System : new FixedClock(now.Value)));
    }

    private async Task<List<CreditLedgerEntry>> LinesAsync(Guid company)
    {
        await using var db = fixture.CreateContext();
        return await db.CreditLedgerEntries.AsNoTracking().Where(l => l.CompanyId == company).OrderBy(l => l.Seq).ToListAsync();
    }

    private async Task<Company> CompanyOf(Guid company)
    {
        await using var db = fixture.CreateContext();
        return await db.Companies.AsNoTracking().SingleAsync(c => c.Id == company);
    }

    private async Task<List<string>> HoldEventsOf(Guid company)
    {
        await using var db = fixture.CreateContext();
        return await db.OutboxMessages.AsNoTracking().Where(m => m.AggregateId == company && m.EventType.StartsWith("credit.hold_"))
            .OrderBy(m => m.OccurredAtUtc).Select(m => m.EventType).ToListAsync();
    }

    private static async Task<string> CodeOf(Func<Task> act) => (await Assert.ThrowsAsync<ConflictException>(act)).Code;

    // ---- CS25: placement -------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task AnOverdueCompany_CannotPlaceANewCreditOrder()
    {
        var v = await VariantAsync();
        // Due 15 days ago (charged 45 days ago on Net-30): exactly 15 days past due, not more: not on hold.
        var edge = await TeamAsync();
        await ChargedOrderAsync(edge, 10_000, daysAgo: 45);
        (await PlaceAsync(edge.Buyer, edge.Company, v)).Status.Should().Be("confirmed");

        // Due 16 days ago: on hold. The credit order is refused with nothing written; a gateway-paid order still goes through.
        var t = await TeamAsync();
        await ChargedOrderAsync(t, 10_000, daysAgo: 46);
        var before = await LinesAsync(t.Company);
        (await CodeOf(() => PlaceAsync(t.Buyer, t.Company, v))).Should().Be("CREDIT_HOLD_OVERDUE");
        (await LinesAsync(t.Company)).Should().HaveCount(before.Count, "a refused order charges nothing");
        (await PlaceAsync(t.Buyer, t.Company, v, onCredit: false)).Status.Should().Be("pending_payment", "the hold never blocks gateway-paid orders");
        await using (var db = fixture.CreateContext())
            (await db.Orders.CountAsync(o => o.CompanyId == t.Company && o.PaysOnCredit && o.UserId == t.Buyer)).Should().Be(0, "no credit order was stored");

        // The owner's "flag only" (0) never holds; a paid-off old order never holds.
        (await PlaceAsync(t.Buyer, t.Company, v, options: new CreditOptions { HoldAfterDaysPastDue = 0 })).Status.Should().Be("confirmed");
        var paid = await TeamAsync();
        await ChargedOrderAsync(paid, 10_000, daysAgo: 90);
        await PaidAsync(paid.Company, 10_000);
        (await PlaceAsync(paid.Buyer, paid.Company, v)).Status.Should().Be("confirmed");
    }

    [PostgresFact]
    public async Task AnOrderApprovedWhileOnHold_IsCancelledNotCharged()
    {
        var t = await TeamAsync(approvalRule: true);
        var v = await VariantAsync();
        var placed = await PlaceAsync(t.Buyer, t.Company, v, qty: 3);
        placed.Status.Should().Be("pending_approval");
        var old = await ChargedOrderAsync(t, 10_000, daysAgo: 60);   // the company falls on hold while the order waits

        // A new order that needs approval is refused at once (an early answer, read without the lock).
        (await CodeOf(() => PlaceAsync(t.Buyer, t.Company, v, qty: 3))).Should().Be("CREDIT_HOLD_OVERDUE");

        await using (var db = fixture.CreateContext())
        {
            var request = await db.ApprovalRequests.AsNoTracking().Where(r => r.SubjectType == "order" && r.SubjectId == placed.OrderId).Select(r => r.Id).SingleAsync();
            await new ApprovalService(db, TimeProvider.System, NewOrders(db, null, null)).DecideAsync(t.Approver, t.Company, request, new DecisionRequest(true, null));
        }

        await using (var db = fixture.CreateContext())
        {
            (await db.Orders.AsNoTracking().Where(o => o.Id == placed.OrderId).Select(o => o.Status).SingleAsync()).Should().Be(OrderStatus.Cancelled);
            (await db.OrderEvents.AsNoTracking().Where(e => e.OrderId == placed.OrderId && e.ToStatus == OrderStatus.Cancelled).Select(e => e.Reason).SingleAsync())
                .Should().Contain("credit on hold: overdue invoices");
        }
        var lines = await LinesAsync(t.Company);
        lines.Should().ContainSingle("the approved order is not charged").Which.OrderId.Should().Be(old);
        (await CompanyOf(t.Company)).CreditUsedMinor.Should().Be(10_000);
    }

    [PostgresFact]
    public async Task OnHold_PaymentsCanStillBeRecordedAndVerified()
    {
        var t = await TeamAsync();
        var v = await VariantAsync();
        await ChargedOrderAsync(t, 30_000, daysAgo: 50);
        (await CodeOf(() => PlaceAsync(t.Buyer, t.Company, v))).Should().Be("CREDIT_HOLD_OVERDUE");
        await Holds(s => s.SweepAsync(DateTime.UtcNow));
        (await CompanyOf(t.Company)).CreditHoldSinceUtc.Should().NotBeNull();

        // Part payment: still overdue. The rest: the hold ends with the verification, before any sweep.
        await PaidAsync(t.Company, 10_000);
        (await CodeOf(() => PlaceAsync(t.Buyer, t.Company, v))).Should().Be("CREDIT_HOLD_OVERDUE");
        await PaidAsync(t.Company, 20_000);
        (await PlaceAsync(t.Buyer, t.Company, v)).Status.Should().Be("confirmed");
        (await CompanyOf(t.Company)).CreditUsedMinor.Should().Be(Unit);
    }

    [PostgresFact]
    public async Task HoldOverride_LetsCreditOrdersThroughForItsDays_ThenExpires_AndIsAudited()
    {
        var t = await TeamAsync();
        var v = await VariantAsync();
        await ChargedOrderAsync(t, 10_000, daysAgo: 50);
        var (admin, now) = (await UserAsync(), DateTime.UtcNow);

        (await Assert.ThrowsAsync<ValidationException>(() => Holds(s => s.OverrideAsync(t.Company, new OverrideCreditHoldRequest(8, "customer promised to pay")), admin)))
            .Errors.Should().ContainSingle(e => e.Field == "days");
        (await Assert.ThrowsAsync<ValidationException>(() => Holds(s => s.OverrideAsync(t.Company, new OverrideCreditHoldRequest(null, "customer promised to pay")), admin)))
            .Errors.Should().ContainSingle(e => e.Field == "days", "a missing number never ends a lift by accident");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Holds(s => s.OverrideAsync(Guid.NewGuid(), new OverrideCreditHoldRequest(1, "customer promised to pay")), admin));
        var pending = await TeamAsync(activate: false);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Holds(s => s.OverrideAsync(pending.Company, new OverrideCreditHoldRequest(1, "customer promised to pay")), admin));

        var lifted = await Holds(s => s.OverrideAsync(t.Company, new OverrideCreditHoldRequest(2, "customer promised to pay")), admin, now);
        lifted.Should().BeEquivalentTo(new { CompanyId = t.Company, OverdueSince = (DateOnly?)Today.AddDays(-20), OnHold = false, OverrideUntilUtc = (DateTime?)now.AddDays(2) });

        (await PlaceAsync(t.Buyer, t.Company, v, now: now.AddDays(2).AddMinutes(-1))).Status.Should().Be("confirmed");
        (await CodeOf(() => PlaceAsync(t.Buyer, t.Company, v, now: now.AddDays(2)))).Should().Be("CREDIT_HOLD_OVERDUE", "the lift has expired");

        var ended = await Holds(s => s.OverrideAsync(t.Company, new OverrideCreditHoldRequest(0, "promise broken again")), admin, now.AddHours(1));
        (ended.OnHold, ended.OverrideUntilUtc).Should().Be((true, (DateTime?)null));
        (await CodeOf(() => PlaceAsync(t.Buyer, t.Company, v, now: now.AddHours(2)))).Should().Be("CREDIT_HOLD_OVERDUE");

        await using var db = fixture.CreateContext();
        var audits = await db.AuditLogs.AsNoTracking().Where(a => a.EntityId == t.Company.ToString() && a.Action == "credit.hold_overridden")
            .OrderBy(a => a.CreatedAtUtc).ToListAsync();
        audits.Should().HaveCount(2);
        audits[0].Should().BeEquivalentTo(new { ActorId = (Guid?)admin, ActorIp = Ip, CorrelationId = "corr-hold", EntityName = "companies" });
        using (var after = JsonDocument.Parse(audits[0].NewValuesJson!))
            (after.RootElement.GetProperty("days").GetInt32(), after.RootElement.GetProperty("reason").GetString()).Should().Be((2, "customer promised to pay"));
        (await db.OutboxMessages.AsNoTracking().CountAsync(m => m.AggregateId == t.Company && m.EventType == "credit.hold_overridden")).Should().Be(2);
    }

    // ---- The Worker sweep --------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task TheSweep_NotesAHoldOnce_WithAnAuditRowAndAnOutboxEvent_AndEndsItWhenPaid()
    {
        var t = await TeamAsync();
        await ChargedOrderAsync(t, 10_000, daysAgo: 50);
        await ChargedOrderAsync(t, 5_000, daysAgo: 2);
        var calm = await TeamAsync();
        await ChargedOrderAsync(calm, 10_000, daysAgo: 40);   // 10 days past due: not held

        // Three passes at once (two Worker replicas and a retry): one note, one event, one audit row.
        using var gate = new ManualResetEventSlim(false);
        var passes = Enumerable.Range(0, 3).Select(_ => Task.Run(async () => { gate.Wait(); return await Holds(s => s.SweepAsync(DateTime.UtcNow)); })).ToList();
        gate.Set();
        await Task.WhenAll(passes);
        await Holds(s => s.SweepAsync(DateTime.UtcNow));

        (await HoldEventsOf(t.Company)).Should().Equal("credit.hold_started");
        (await HoldEventsOf(calm.Company)).Should().BeEmpty();
        (await CompanyOf(t.Company)).CreditHoldSinceUtc.Should().NotBeNull();
        await using (var db = fixture.CreateContext())
        {
            var audit = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.EntityId == t.Company.ToString() && a.Action == "credit.hold_started");
            (audit.ActorId, audit.EntityName).Should().Be(((Guid?)null, "companies"));
            using var after = JsonDocument.Parse(audit.NewValuesJson!);
            after.RootElement.GetProperty("overdueSince").GetString().Should().Be(Today.AddDays(-20).ToString("yyyy-MM-dd"));
            var started = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.AggregateId == t.Company && m.EventType == "credit.hold_started");
            after.RootElement.GetProperty("eventId").GetGuid().Should().Be(started.Id);
        }

        await PaidAsync(t.Company, 10_000);   // pays the oldest due first
        await Holds(s => s.SweepAsync(DateTime.UtcNow));
        await Holds(s => s.SweepAsync(DateTime.UtcNow));
        (await HoldEventsOf(t.Company)).Should().Equal("credit.hold_started", "credit.hold_ended");
        (await CompanyOf(t.Company)).CreditHoldSinceUtc.Should().BeNull();

        // "Flag only" (0) ends every noted hold.
        var flagged = await TeamAsync();
        await ChargedOrderAsync(flagged, 10_000, daysAgo: 50);
        await Holds(s => s.SweepAsync(DateTime.UtcNow));
        await Holds(s => s.SweepAsync(DateTime.UtcNow), options: new CreditOptions { HoldAfterDaysPastDue = 0 });
        (await HoldEventsOf(flagged.Company)).Should().Equal("credit.hold_started", "credit.hold_ended");
    }

    // ---- CS16 / CS25: under the company lock ---------------------------------------------------------------------------------

    [PostgresFact]
    public async Task APlacementWaitingForTheCompanyLock_SeesAnOverdueDebtWrittenMeanwhile_AndIsRefused()
    {
        // A writer holds the company and posts a charge that is already overdue (as a bounce does: payment → company, then a line).
        // The placement's early, unlocked read sees nothing; the check under the lock must see the committed line.
        var t = await TeamAsync();
        var v = await VariantAsync();
        await using (var writer = Ctx())
        {
            await using var tx = await writer.Database.BeginTransactionAsync();
            await ChargeAsync(writer, t, 10_000, daysAgo: 60, locked: true);
            var placement = Task.Run(() => PlaceAsync(t.Buyer, t.Company, v));
            (await Task.WhenAny(placement, Task.Delay(TimeSpan.FromSeconds(2)))).Should().NotBeSameAs(placement, "it waits for the company");
            await tx.CommitAsync();
            (await CodeOf(() => placement)).Should().Be("CREDIT_HOLD_OVERDUE");
        }
        (await LinesAsync(t.Company)).Should().ContainSingle();
    }

    [PostgresFact]
    public async Task ALift_WaitsForAWriterHoldingTheCompany_ThenSucceedsOnTheNewRow()
    {
        // A placement or verification holds the company and moves its credit; a lift sent meanwhile queues on the same row lock and then
        // works on the committed row (an unlocked read would carry the old row version and fail on the concurrency token).
        var t = await TeamAsync();
        await ChargedOrderAsync(t, 10_000, daysAgo: 50);
        var admin = await UserAsync();
        await using (var writer = Ctx())
        {
            await using var tx = await writer.Database.BeginTransactionAsync();
            await ChargeAsync(writer, t, 5_000, daysAgo: 1, locked: true);
            var lift = Task.Run(() => Holds(s => s.OverrideAsync(t.Company, new OverrideCreditHoldRequest(1, "customer promised to pay")), admin));
            (await Task.WhenAny(lift, Task.Delay(TimeSpan.FromSeconds(2)))).Should().NotBeSameAs(lift, "it waits for the company");
            await tx.CommitAsync();
            (await lift).OnHold.Should().BeFalse();
        }
        var company = await CompanyOf(t.Company);
        (company.CreditUsedMinor, company.CreditHoldOverrideUntilUtc is not null).Should().Be((15_000, true));
    }

    [PostgresFact]
    public async Task SweepVerifyBounceAndPlacement_InParallel_NeverDeadlock_AndNoChargeIsPostedWhileOnHold()
    {
        // One company, all at once: 4 sweeps (company only), 2 verifications that pay the overdue orders and 1 bounce of an older payment
        // that makes a third order owe again (payment → company), and 6 credit placements (price lists → company). No deadlock; every
        // placement is either charged or refused with CREDIT_HOLD_OVERDUE; replaying the ledger line by line, no placement's charge was
        // posted while an order owed more than 15 days past due; a final sweep agrees with the ledger.
        var t = await TeamAsync(buyers: 6);
        var variants = new List<Guid>();
        for (var i = 0; i < 6; i++) variants.Add(await VariantAsync(5));
        var bounced = await ChargedOrderAsync(t, 20_000, daysAgo: 70);
        var bouncing = await PaidAsync(t.Company, 20_000);                // pays it; its bounce makes it owe again, overdue at once
        await ChargedOrderAsync(t, 30_000, daysAgo: 60);
        await ChargedOrderAsync(t, 40_000, daysAgo: 50);
        await Holds(s => s.SweepAsync(DateTime.UtcNow));
        var recorder = await UserAsync();
        var toVerify = new List<Guid>();
        foreach (var amount in new[] { 30_000L, 40_000L })
            toVerify.Add((await Pay(recorder, s => s.RecordAsync(t.Company, new RecordCreditPaymentRequest("npsb", amount, Today, Ref()), Key()))).Payment.Id);
        var (verifier, bouncer) = (await UserAsync(), await UserAsync());

        using var gate = new ManualResetEventSlim(false);
        var placed = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        var refused = 0;
        var work = new List<Task>();
        work.AddRange(Enumerable.Range(0, 4).Select(_ => Task.Run(async () => { gate.Wait(); await Holds(s => s.SweepAsync(DateTime.UtcNow)); })));
        work.AddRange(toVerify.Select(p => Task.Run(async () => { gate.Wait(); await Pay(verifier, s => s.VerifyAsync(p, new VerifyCreditPaymentRequest(Today))); })));
        work.Add(Task.Run(async () => { gate.Wait(); await Pay(bouncer, s => s.BounceAsync(bouncing, new BounceCreditPaymentRequest("cheque returned: refer to drawer"))); }));
        work.AddRange(t.Buyers.Select((b, i) => Task.Run(async () =>
        {
            gate.Wait();
            try { placed.Add((await PlaceAsync(b, t.Company, variants[i])).OrderId); }
            catch (ConflictException ex) when (ex.Code == "CREDIT_HOLD_OVERDUE") { Interlocked.Increment(ref refused); }
        })));
        gate.Set();
        var all = Task.WhenAll(work);
        (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(120)))).Should().BeSameAs(all, "a deadlock would hang or abort");
        await all;   // any 40P01, lost update or broken chain throws here

        (placed.Count + refused).Should().Be(6);
        var lines = await LinesAsync(t.Company);
        var company = await CompanyOf(t.Company);
        company.CreditUsedMinor.Should().Be(lines.Sum(l => l.ReceivableDeltaMinor));
        var dueBefore = new CreditOptions().HoldDueBefore(DateTime.UtcNow)!.Value;
        var owed = new Dictionary<Guid, long>();
        var due = new Dictionary<Guid, DateOnly>();
        foreach (var l in lines)
        {
            if (l.Kind == CreditLedgerKind.Charge && placed.Contains(l.OrderId!.Value))
                owed.Where(o => o.Value > 0 && due[o.Key] < dueBefore).Should().BeEmpty($"the charge at seq {l.Seq} was posted while on hold");
            if (l.OrderId is not { } order) continue;
            owed[order] = owed.GetValueOrDefault(order) + l.ReceivableDeltaMinor;
            if (l.DueOn is { } d) due[order] = d;
        }
        // 90,000 owed on the three old orders (the bounce puts 20,000 back), 70,000 verified, oldest due first: 20,000 stays overdue.
        owed.Where(o => due[o.Key] < dueBefore).Sum(o => o.Value).Should().Be(20_000);
        owed.ContainsKey(bounced).Should().BeTrue();

        await Holds(s => s.SweepAsync(DateTime.UtcNow));
        (await CompanyOf(t.Company)).CreditHoldSinceUtc.Should().NotBeNull("the bounced order is 40 days past due");
        var events = await HoldEventsOf(t.Company);
        events.Should().NotBeEmpty().And.Subject.First().Should().Be("credit.hold_started");
        events.Last().Should().Be("credit.hold_started");
        events.Zip(events.Skip(1)).Should().OnlyContain(p => p.First != p.Second, "starts and ends alternate");
    }
}
