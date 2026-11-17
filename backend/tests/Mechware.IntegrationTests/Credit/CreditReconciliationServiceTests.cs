using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Companies;
using Mechware.Application.Credit;
using Mechware.Application.Identity;
using Mechware.Application.Orders;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
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
/// B3.10, threat model §১২ CS28 against a real database with every ledger guard installed: books written only by the real services
/// (placements, verifications, rejections, bounces, applies, cancellations, adjustments, the hold sweep) give no finding at all, also
/// while those writers run in parallel with several reconciliation passes, and a pass takes no row lock and writes nothing. Every test
/// of this class keeps the whole database consistent, so "no finding" is asserted over every company, not only the test's own.
/// </summary>
public class CreditReconciliationServiceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const long Unit = 100_000;            // the test variant's price
    private static long _binSeq = 4_100_000_000;
    private static DateOnly Today => CreditCalendar.DhakaDate(DateTime.UtcNow);
    private static string Ref() => "TRX" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
    private static string Key() => "key-" + Guid.NewGuid().ToString("N");

    internal sealed class Staff(Guid id) : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => "203.0.113.10";
        public string? UserAgent => null;
        public string? CorrelationId => "corr-recon";
        public bool HasPermission(string permission) => true;
    }

    internal sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private MechwareDbContext Ctx() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(fixture.ConnectionString)
        .AddInterceptors(new DomainEventOutboxInterceptor()).Options);

    private sealed record Team(Guid Company, Guid Admin, IReadOnlyList<Guid> Buyers);

    private async Task<Team> TeamAsync(int buyers = 1)
    {
        var admin = await UserAsync();
        var buyerIds = new List<Guid>();
        for (var i = 0; i < buyers; i++) buyerIds.Add(await UserAsync());
        await using var db = fixture.CreateContext();
        var companies = new CompanyService(db);
        var company = (await companies.RegisterAsync(admin, new RegisterCompanyRequest("Reconciled Mills Ltd", "TL-B310-1",
            (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13"), null, null))).Id;
        foreach (var buyer in buyerIds)
            await companies.AddAcceptedMemberAsync(admin, company, new AddMemberRequest(buyer, CompanyRoles.Buyer, null, null));
        await companies.AdminActivateAsync(company);
        await companies.AdminSetCreditAsync(company, new SetCreditRequest(CreditTerms.Net30, 100_000_000));
        return new Team(company, admin, buyerIds);
    }

    private Task<Guid> UserAsync() => TestUsers.CreateAsync(fixture);

    private async Task<Guid> ChargedOrderAsync(Team t, long amount, int daysAgo = 2)
    {
        await using var db = Ctx();
        await using var tx = await db.Database.BeginTransactionAsync();
        var at = DateTime.UtcNow.AddDays(-daysAgo);
        var order = await CreditTestData.InsertOrderAsync(db, t.Admin, t.Company, amount, confirmedAtUtc: at);
        var company = (await db.Companies.FromSqlInterpolated($"SELECT c.*, c.xmin FROM companies c WHERE c.id = {t.Company} FOR UPDATE").ToListAsync()).Single();
        var lines = await db.CreditLedgerEntries.Where(l => l.CompanyId == t.Company).ToListAsync();
        db.CreditLedgerEntries.Add(new CreditLedger(company, lines).Charge(order, amount, at, at).Entry);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return order;
    }

    private async Task<Guid> VariantAsync(int onHand = 50)
    {
        await using var db = fixture.CreateContext();
        return (await CatalogTestData.SeedVariantAsync(db, onHand, priceMinor: Unit)).VariantId;
    }

    private async Task<PlacedOrder> PlaceAsync(Guid buyer, Guid company, Guid variant)
    {
        await using var db = fixture.CreateContext();
        var orders = new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), TimeProvider.System,
            ShippingTestKit.DevelopmentQuoter(db), new Infrastructure.Pricing.PriceListResolver(db), credit: new CreditOptions());
        return await orders.PlaceAsync(new PlaceOrderRequest(buyer, Key(), [new PlaceOrderLine(variant, 1)], null, company, PayOnCredit: true));
    }

    private async Task<T> Pay<T>(Guid actor, Func<CreditPaymentService, Task<T>> action, DateTime? now = null)
    {
        await using var db = Ctx();
        return await action(new CreditPaymentService(db, new Staff(actor), new CreditOptions(), now is null ? TimeProvider.System : new FixedClock(now.Value)));
    }

    private async Task<Guid> RecordedAsync(Guid company, long amount, string method = "rtgs", int daysAgo = 0, DateOnly? chequeDate = null)
    {
        var request = method == "cheque"
            ? new RecordCreditPaymentRequest(method, amount, Today.AddDays(-daysAgo), "CHQ" + Ref(), "Sonali Bank", ChequeDate: chequeDate ?? Today.AddDays(-daysAgo))
            : new RecordCreditPaymentRequest(method, amount, Today.AddDays(-daysAgo), method == "cash" ? null : Ref());
        return (await Pay(await UserAsync(), s => s.RecordAsync(company, request, Key()), DateTime.UtcNow.AddDays(-daysAgo))).Payment.Id;
    }

    private async Task<Guid> PaidAsync(Guid company, long amount, string method = "rtgs")
    {
        var payment = await RecordedAsync(company, amount, method);
        await Pay(await UserAsync(), s => s.VerifyAsync(payment, new VerifyCreditPaymentRequest(Today)));
        return payment;
    }

    private async Task BounceAsync(Guid payment)
    {
        var bouncer = await UserAsync();
        await Pay(bouncer, s => s.BounceAsync(payment, new BounceCreditPaymentRequest("cheque returned: refer to drawer")));
    }

    /// <summary>What E7.2 does: lock the order (NO KEY UPDATE), reduce the charge, set the order cancelled, one commit.</summary>
    private async Task CancelAsync(Guid order)
    {
        await using var db = Ctx();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM orders WHERE id = {order} FOR NO KEY UPDATE");
        await new CreditLedgerService(db, new Staff(Guid.NewGuid()), TimeProvider.System).ReduceChargeAsync(
            new CreditReduction(order, CreditReductionKind.OrderCancelled, null, $"cancel:{order:N}", null, null, "cancelled by support"));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE orders SET status = 'Cancelled' WHERE id = {order}");
        await tx.CommitAsync();
    }

    private async Task<T> Adjust<T>(Guid actor, Func<CreditAdjustmentService, Task<T>> action)
    {
        await using var db = Ctx();
        return await action(new CreditAdjustmentService(db, new Staff(actor), new CreditOptions { PayoutAfterClearingDays = 0 }, TimeProvider.System));
    }

    private async Task<Guid> AdjustmentAsync(Guid company, RequestCreditAdjustmentRequest request, bool approve = true)
    {
        var id = (await Adjust(await UserAsync(), s => s.RequestAsync(company, request, Key()))).Adjustment.Id;
        if (approve) await Adjust(await UserAsync(), s => s.ApproveAsync(id));
        return id;
    }

    private async Task SweepAsync()
    {
        await using var db = Ctx();
        await new CreditHoldService(db, new Staff(Guid.NewGuid()), new CreditOptions(), TimeProvider.System).SweepAsync(DateTime.UtcNow);
    }

    private async Task<CreditReconciliationReport> ReconcileAsync(DateTime? now = null)
    {
        await using var db = Ctx();
        return await new CreditReconciliationService(db, new CreditOptions()).RunAsync(now ?? DateTime.UtcNow);
    }

    private async Task<(int Lines, int Audits, int Events)> WrittenAsync()
    {
        await using var db = fixture.CreateContext();
        return (await db.CreditLedgerEntries.CountAsync(), await db.AuditLogs.CountAsync(a => a.Action == CreditReconciliationService.MismatchEvent),
            await db.OutboxMessages.CountAsync(m => m.EventType == CreditReconciliationService.MismatchEvent));
    }

    // ---- CS28 -----------------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Reconciliation_OnAConsistentBook_ReportsNothing()
    {
        // Every writer of the ledger once: two placements, an overdue charge noted by the sweep, payments that pay, go on account,
        // are rejected, bounce or only wait; an apply, a cancellation that moves paid money on account, a write-off, a correction,
        // a payout and a request still waiting.
        var t = await TeamAsync(buyers: 2);
        var v = await VariantAsync();
        await PlaceAsync(t.Buyers[0], t.Company, v);
        var old = await ChargedOrderAsync(t, 40_000, daysAgo: 50);
        await SweepAsync();
        var cancelled = await ChargedOrderAsync(t, 30_000);
        var writtenOff = await ChargedOrderAsync(t, 20_000);
        await PaidAsync(t.Company, 50_000);                          // the oldest due first: the overdue 40,000, then the next due
        await SweepAsync();                                          // the hold ends
        var bouncing = await PaidAsync(t.Company, 15_000);
        await BounceAsync(bouncing);
        var cash = await PaidAsync(t.Company, 300_000, "cash");      // pays all that is open, the rest on account
        await PlaceAsync(t.Buyers[1], t.Company, v);
        await Pay(await UserAsync(), s => s.ApplyOnAccountAsync(t.Company, new ApplyOnAccountRequest(), Key()));
        await CancelAsync(cancelled);                                // paid: its money goes back on account
        await AdjustmentAsync(t.Company, new RequestCreditAdjustmentRequest("on_account_correction", -1_000, "bank charge deducted by the customer", PaymentId: cash));
        await AdjustmentAsync(t.Company, new RequestCreditAdjustmentRequest("on_account_payout", 2_000, "customer asked for the money back", PaymentId: cash, PayoutReference: "MWOUT12345"));
        await AdjustmentAsync(t.Company, new RequestCreditAdjustmentRequest("on_account_payout", 1_000, "waiting for the second Admin", PaymentId: cash, PayoutReference: "MWOUT67890"), approve: false);
        var rejected = await RecordedAsync(t.Company, 9_000);
        await Pay(await UserAsync(), s => s.RejectAsync(rejected, new RejectCreditPaymentRequest("not in the bank statement")));

        // A second company with a write-off of an order still open, a payment that waits 8 days and a post-dated cheque due today.
        var u = await TeamAsync();
        var open = await ChargedOrderAsync(u, 20_000);
        await AdjustmentAsync(u.Company, new RequestCreditAdjustmentRequest("write_off", 5_000, "damaged in transit, agreed with the buyer", OrderId: open));
        await RecordedAsync(u.Company, 7_000, daysAgo: 8);
        await RecordedAsync(u.Company, 7_000, daysAgo: 6);
        await RecordedAsync(u.Company, 8_000, "cheque", daysAgo: 3, chequeDate: Today);
        await RecordedAsync(u.Company, 8_000, "cheque", daysAgo: 3, chequeDate: Today.AddDays(1));
        writtenOff.Should().NotBeEmpty();

        // A third company paid its overdue order a moment ago and the sweep (every 15 minutes) has not run since: its note lags, which is fine.
        var w = await TeamAsync();
        await ChargedOrderAsync(w, 10_000, daysAgo: 50);
        await SweepAsync();
        await PaidAsync(w.Company, 10_000);

        var before = await WrittenAsync();
        var report = await ReconcileAsync();

        report.Findings.Should().BeEmpty();
        (report.RunOn, report.NewlyRecorded).Should().Be((Today, 0));
        report.CompaniesChecked.Should().BeGreaterThanOrEqualTo(2);
        (report.PaymentsWaitingOver7Days, report.PostDatedChequesDue).Should().Be((1, 1));
        (await WrittenAsync()).Should().Be(before, "a reconciliation writes no line and, without findings, nothing else");
        await using var db = fixture.CreateContext();
        (await db.Orders.AsNoTracking().Where(o => o.Id == old).Select(o => o.Status).SingleAsync()).Should().Be(Domain.Orders.OrderStatus.Confirmed);
    }

    [PostgresFact]
    public async Task ParallelRunsVerificationsBouncesCancelsAndPlacements_GiveNoFalsePositives()
    {
        // One company, all at once: 4 reconciliation loops (3 passes each), 3 verifications, 2 bounces, 2 cancellations of orders the
        // verifications may be paying, 4 credit placements, an apply and 2 hold sweeps. Every pass sees each company between two commits,
        // so none may report anything; nor may a final pass.
        var t = await TeamAsync(buyers: 4);
        var variants = new List<Guid>();
        for (var i = 0; i < 4; i++) variants.Add(await VariantAsync(5));
        var orders = new List<Guid>();
        foreach (var amount in new[] { 20_000L, 30_000L, 40_000L, 50_000L }) orders.Add(await ChargedOrderAsync(t, amount, daysAgo: 5));
        var bouncing = new[] { await PaidAsync(t.Company, 25_000), await PaidAsync(t.Company, 10_000) };
        var toVerify = new List<Guid>();
        foreach (var amount in new[] { 15_000L, 35_000L, 60_000L }) toVerify.Add(await RecordedAsync(t.Company, amount));
        var (verifier, bouncer) = (await UserAsync(), await UserAsync());

        using var gate = new ManualResetEventSlim(false);
        var reports = new System.Collections.Concurrent.ConcurrentBag<CreditReconciliationReport>();
        var work = new List<Task>();
        work.AddRange(Enumerable.Range(0, 4).Select(_ => Task.Run(async () => { gate.Wait(); for (var i = 0; i < 3; i++) reports.Add(await ReconcileAsync()); })));
        work.AddRange(toVerify.Select(p => Task.Run(async () => { gate.Wait(); await Pay(verifier, s => s.VerifyAsync(p, new VerifyCreditPaymentRequest(Today))); })));
        work.AddRange(bouncing.Select(p => Task.Run(async () => { gate.Wait(); await Pay(bouncer, s => s.BounceAsync(p, new BounceCreditPaymentRequest("returned by the bank"))); })));
        work.AddRange(orders.Take(2).Select(o => Task.Run(async () => { gate.Wait(); await CancelAsync(o); })));
        work.AddRange(t.Buyers.Select((b, i) => Task.Run(async () => { gate.Wait(); await PlaceAsync(b, t.Company, variants[i]); })));
        work.Add(Task.Run(async () =>
        {
            gate.Wait();
            try { await Pay(await UserAsync(), s => s.ApplyOnAccountAsync(t.Company, new ApplyOnAccountRequest(), Key())); }
            catch (ConflictException ex) when (ex.Code == "CREDIT_NOTHING_TO_APPLY") { }
        }));
        work.AddRange(Enumerable.Range(0, 2).Select(_ => Task.Run(async () => { gate.Wait(); await SweepAsync(); })));
        gate.Set();
        var all = Task.WhenAll(work);
        (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(120)))).Should().BeSameAs(all, "nothing may wait on a reconciliation");
        await all;

        reports.Should().HaveCount(12);
        reports.SelectMany(r => r.Findings).Should().BeEmpty("a pass never sees half of a commit");
        (await ReconcileAsync()).Findings.Should().BeEmpty();
        (await WrittenAsync()).Audits.Should().Be(0);
    }

    [PostgresFact]
    public async Task Reconciliation_TakesNoRowLock_SoAWriterHoldingTheRowsNeverStopsIt()
    {
        // A writer holds the company, a payment and an order FOR UPDATE (stronger than any credit writer): the pass still finishes, and it
        // reads the books as they were before that writer.
        var t = await TeamAsync();
        var order = await ChargedOrderAsync(t, 10_000);
        var payment = await RecordedAsync(t.Company, 5_000);
        await using var writer = Ctx();
        await using var tx = await writer.Database.BeginTransactionAsync();
        await writer.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM credit_payments WHERE id = {payment} FOR UPDATE");
        await writer.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM orders WHERE id = {order} FOR UPDATE");
        await writer.Database.ExecuteSqlInterpolatedAsync($"UPDATE companies SET credit_limit = credit_limit + 1 WHERE id = {t.Company}");

        var pass = ReconcileAsync();
        (await Task.WhenAny(pass, Task.Delay(TimeSpan.FromSeconds(20)))).Should().BeSameAs(pass, "it never waits for a row lock");
        (await pass).Findings.Should().BeEmpty();
        await tx.RollbackAsync();
    }
}
