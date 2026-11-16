using System.Text.Json;
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
/// B3.5b, threat model §১২ CS9 (apply on-account, oldest source first), CS29 (its audit row) and CS16 (its place in the lock order)
/// against a real database with every ledger guard installed: the company row is the only lock it takes; the lines it writes name
/// the orders and the source payments, so a cancel or a bounce holding those rows (NO KEY UPDATE) never blocks it.
/// </summary>
public class CreditOnAccountServiceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string Ip = "203.0.113.17";
    private static long _binSeq = 3_900_000_000;
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

    private MechwareDbContext Ctx() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(fixture.ConnectionString)
        .AddInterceptors(new DomainEventOutboxInterceptor()).Options);

    private async Task<T> As<T>(Guid actor, Func<CreditPaymentService, Task<T>> action, string? correlationId = null)
    {
        await using var db = Ctx();
        return await action(new CreditPaymentService(db, new Staff(actor, correlationId ?? "corr-" + Guid.NewGuid().ToString("N")[..8]), new CreditOptions(), TimeProvider.System));
    }

    private sealed record Team(Guid Company, Guid Admin, IReadOnlyList<Guid> Buyers);

    private async Task<Team> TeamAsync(long limitMinor = 10_000_000, int buyers = 0, bool activate = true)
    {
        var admin = await TestUsers.CreateAsync(fixture);
        var buyerIds = new List<Guid>();
        for (var i = 0; i < buyers; i++) buyerIds.Add(await TestUsers.CreateAsync(fixture));
        await using var db = fixture.CreateContext();
        var companies = new CompanyService(db);
        var company = (await companies.RegisterAsync(admin, new RegisterCompanyRequest("On Account Traders Ltd", "TL-B35B-1",
            (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13"), null, null))).Id;
        foreach (var buyer in buyerIds)
            await companies.AddAcceptedMemberAsync(admin, company, new AddMemberRequest(buyer, CompanyRoles.Buyer, null, null));
        if (!activate) return new Team(company, admin, buyerIds);
        await companies.AdminActivateAsync(company);
        await companies.AdminSetCreditAsync(company, new SetCreditRequest(CreditTerms.Net30, limitMinor));
        return new Team(company, admin, buyerIds);
    }

    private async Task<Guid> ChargedOrderAsync(Team t, long amount, int daysAgo = 5)
    {
        await using var db = fixture.CreateContext();
        var order = await CreditTestData.InsertOrderAsync(db, t.Admin, t.Company, amount);
        var company = await db.Companies.SingleAsync(c => c.Id == t.Company);
        var at = DateTime.UtcNow.AddDays(-daysAgo);
        db.CreditLedgerEntries.Add(new CreditLedger(company, []).Charge(order, amount, at, at).Entry);
        await db.SaveChangesAsync();
        return order;
    }

    private Task<Guid> UserAsync() => TestUsers.CreateAsync(fixture);

    /// <summary>Recorded by one person, verified by another (with nothing open, all of it goes on account); returns the payment.</summary>
    private async Task<Guid> PaidAsync(Team t, long amount, string method = "rtgs")
    {
        var (recorder, verifier) = (await UserAsync(), await UserAsync());
        var request = method == "cheque"
            ? new RecordCreditPaymentRequest(method, amount, Today, "CHQ" + Ref(), "Sonali Bank", ChequeDate: Today)
            : new RecordCreditPaymentRequest(method, amount, Today, Ref());
        var payment = (await As(recorder, s => s.RecordAsync(t.Company, request, Key()))).Payment.Id;
        await As(verifier, s => s.VerifyAsync(payment, new VerifyCreditPaymentRequest(Today)));
        return payment;
    }

    private Task<AppliedOnAccount> ApplyAsync(Guid actor, Guid company, ApplyOnAccountRequest request, string? key = null, string? corr = null) =>
        As(actor, s => s.ApplyOnAccountAsync(company, request, key ?? Key()), corr);

    private Task<BouncedCreditPayment> BounceAsync(Guid actor, Guid payment) =>
        As(actor, s => s.BounceAsync(payment, new BounceCreditPaymentRequest("cheque returned: account closed")));

    private async Task<CreditReductionResult?> ReduceAsync(Guid order)
    {
        await using var db = Ctx();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM orders WHERE id = {order} FOR NO KEY UPDATE");
        var result = await new CreditLedgerService(db, new Staff(Guid.NewGuid(), "corr-cancel"), TimeProvider.System)
            .ReduceChargeAsync(new CreditReduction(order, CreditReductionKind.OrderCancelled, null, $"cancel:{order:N}", null, null, "order cancelled"));
        await tx.CommitAsync();
        return result;
    }

    private async Task<(Company Company, List<CreditLedgerEntry> Lines)> StateAsync(Guid company)
    {
        await using var db = fixture.CreateContext();
        return (await db.Companies.AsNoTracking().SingleAsync(c => c.Id == company),
            await db.CreditLedgerEntries.AsNoTracking().Where(l => l.CompanyId == company).OrderBy(l => l.Seq).ToListAsync());
    }

    /// <summary>I1, I2 (also per source), I4 and the chain over every line of the company, from the database.</summary>
    private async Task<List<CreditLedgerEntry>> LedgerAddsUpAsync(Guid companyId)
    {
        var (company, lines) = await StateAsync(companyId);
        lines.Select(l => l.Seq).Should().Equal(Enumerable.Range(1, lines.Count).Select(i => (long)i));
        company.CreditUsedMinor.Should().Be(lines.Sum(l => l.ReceivableDeltaMinor));
        company.CreditOnAccountMinor.Should().Be(lines.Sum(l => l.OnAccountDeltaMinor));
        CreditOnAccount.Sources(lines).Sum(s => s.OnAccountMinor).Should().Be(company.CreditOnAccountMinor, "no source below zero");
        var ledger = new CreditLedger(company, lines);
        foreach (var order in lines.Where(l => l.OrderId is not null).Select(l => l.OrderId!.Value).Distinct())
        {
            var b = ledger.BalanceOf(order);
            (b.ReceivableMinor >= 0 && b.PaidMinor >= 0 && b.ReducedMinor <= b.ChargeMinor).Should().BeTrue($"order {order}: {b}");
        }
        return lines;
    }

    /// <summary>I5: what a payment's money is now (paid on orders + still on account).</summary>
    private static long MoneyOf(IEnumerable<CreditLedgerEntry> lines, Guid payment) => lines.Where(l => l.PaymentId == payment)
        .Sum(l => (l.OrderId is null ? 0 : l.Kind == CreditLedgerKind.ChargeReduced ? -l.OnAccountDeltaMinor : -l.ReceivableDeltaMinor) + l.OnAccountDeltaMinor);

    private static async Task<string> CodeOf(Func<Task> act) => (await Assert.ThrowsAsync<ConflictException>(act)).Code;

    // ---- CS9 ----------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task ApplyOnAccount_UsesTheOldestSourceFirst()
    {
        // Two transfers arrived before any order (40,000 then 50,000, all on account: available credit did not rise). Applying 70,000
        // pays the order due first, then the next, and spends the OLDER transfer first; one line per (order, source).
        var t = await TeamAsync(limitMinor: 1_000_000);
        var p1 = await PaidAsync(t, 40_000);
        var p2 = await PaidAsync(t, 50_000, method: "cheque");
        var later = await ChargedOrderAsync(t, 50_000, daysAgo: 1);
        var early = await ChargedOrderAsync(t, 30_000, daysAgo: 20);
        (await StateAsync(t.Company)).Company.CreditAvailable.ToMinorUnits().Should().Be(920_000, "on-account money does not raise available credit (CS9)");
        var (finance, before) = (await UserAsync(), await StateAsync(t.Company));

        var result = await ApplyAsync(finance, t.Company, new ApplyOnAccountRequest(70_000), corr: "corr-apply");

        result.Applied.Should().Equal(new CreditOnAccountApplication(early, p1, null, 30_000), new CreditOnAccountApplication(later, p1, null, 10_000),
            new CreditOnAccountApplication(later, p2, null, 30_000));
        (result.AppliedMinor, result.CreditUsedMinor, result.CreditOnAccountMinor, result.Replayed).Should().Be((70_000, 10_000, 20_000, false));
        var lines = await LedgerAddsUpAsync(t.Company);
        lines.Take(before.Lines.Count).Should().BeEquivalentTo(before.Lines, "applying never edits an old line");
        lines.Skip(before.Lines.Count).Should().HaveCount(3).And.OnlyContain(l => l.Kind == CreditLedgerKind.OnAccountApplied && l.ActorUserId == finance
            && l.CorrelationId == "corr-apply" && l.EventId == result.EventId && l.OccurredOn == Today && l.IdempotencyKey.StartsWith("apply:"));
        (await StateAsync(t.Company)).Company.CreditAvailable.ToMinorUnits().Should().Be(990_000);
        (MoneyOf(lines, p1), MoneyOf(lines, p2)).Should().Be((40_000, 50_000), "money moves, it is never created or lost (I5)");

        // The older transfer bounces: its application is undone on both orders; the cheque's 20,000 stays on account.
        (await BounceAsync(await UserAsync(), p1)).Should().BeEquivalentTo(new
        {
            Reversed = new[] { new CreditAllocation(early, 30_000), new CreditAllocation(later, 10_000) },
            OnAccountReversedMinor = 0L,
            CreditUsedMinor = 50_000L,
            CreditOnAccountMinor = 20_000L
        });
        CreditOnAccount.Sources(await LedgerAddsUpAsync(t.Company)).Should().ContainSingle().Which.Should().BeEquivalentTo(new { PaymentId = (Guid?)p2, OnAccountMinor = 20_000L });
    }

    [PostgresFact]
    public async Task ApplyOnAccount_WithNamedOrders_OrAll_AndRefusals_PostNothingWrong()
    {
        var t = await TeamAsync();
        var finance = await UserAsync();
        (await CodeOf(() => ApplyAsync(finance, t.Company, new ApplyOnAccountRequest()))).Should().Be("CREDIT_NOTHING_TO_APPLY");
        var p = await PaidAsync(t, 100_000);
        var (old, young) = (await ChargedOrderAsync(t, 30_000, daysAgo: 30), await ChargedOrderAsync(t, 50_000, daysAgo: 2));

        (await CodeOf(() => ApplyAsync(finance, t.Company, new ApplyOnAccountRequest(100_001)))).Should().Be("CREDIT_ON_ACCOUNT_INSUFFICIENT");
        (await CodeOf(() => ApplyAsync(finance, t.Company, new ApplyOnAccountRequest(90_000)))).Should().Be("CREDIT_ON_ACCOUNT_EXCEEDS_OPEN");
        (await CodeOf(() => ApplyAsync(finance, t.Company, new ApplyOnAccountRequest(null, [new(young, 50_001)])))).Should().Be("ALLOCATION_EXCEEDS_OUTSTANDING");
        (await CodeOf(() => ApplyAsync(finance, t.Company, new ApplyOnAccountRequest(null, [new(Guid.NewGuid(), 1)])))).Should().Be("ALLOCATION_ORDER_NOT_OPEN");
        await Assert.ThrowsAsync<ValidationException>(() => ApplyAsync(finance, t.Company, new ApplyOnAccountRequest(0)));
        (await StateAsync(t.Company)).Lines.Should().NotContain(l => l.Kind == CreditLedgerKind.OnAccountApplied, "a refusal posts nothing");

        (await ApplyAsync(finance, t.Company, new ApplyOnAccountRequest(null, [new(young, 20_000)]))).Applied
            .Should().Equal(new CreditOnAccountApplication(young, p, null, 20_000));
        (await ApplyAsync(finance, t.Company, new ApplyOnAccountRequest())).Applied
            .Should().Equal(new CreditOnAccountApplication(old, p, null, 30_000), new CreditOnAccountApplication(young, p, null, 30_000));
        var lines = await LedgerAddsUpAsync(t.Company);
        var company = (await StateAsync(t.Company)).Company;
        (company.CreditUsedMinor, company.CreditOnAccountMinor, MoneyOf(lines, p)).Should().Be((0, 20_000, 100_000));
    }

    [PostgresFact]
    public async Task ApplyOnAccount_ReplayWithTheSameKey_AppliesOnce_AndAnotherBodyIs409()
    {
        // A double click sends the same request four times at once: the company lock lets one through, the others find its lines
        // and get the same answer. The key is the person's own, per company; another body under it is refused.
        var t = await TeamAsync();
        await PaidAsync(t, 60_000);
        await ChargedOrderAsync(t, 20_000, daysAgo: 10);
        await ChargedOrderAsync(t, 20_000, daysAgo: 5);
        var finance = await UserAsync();
        const string key = "apply-replay-0001";

        using var gate = new ManualResetEventSlim(false);
        var racers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            gate.Wait();
            return await ApplyAsync(finance, t.Company, new ApplyOnAccountRequest(25_000), key);
        })).ToList();
        gate.Set();
        var outcomes = await Task.WhenAll(racers);

        outcomes.Count(o => !o.Replayed).Should().Be(1);
        outcomes.Select(o => (o.EventId, o.AppliedMinor)).Distinct().Should().ContainSingle();
        outcomes.Should().OnlyContain(o => o.Applied.SequenceEqual(outcomes[0].Applied));
        (await LedgerAddsUpAsync(t.Company)).Count(l => l.Kind == CreditLedgerKind.OnAccountApplied).Should().Be(2);

        (await CodeOf(() => ApplyAsync(finance, t.Company, new ApplyOnAccountRequest(25_001), key))).Should().Be("IDEMPOTENCY_KEY_REUSED");
        (await CodeOf(() => ApplyAsync(finance, t.Company, new ApplyOnAccountRequest(), key))).Should().Be("IDEMPOTENCY_KEY_REUSED");
        // Another person's key of the same spelling is their own request.
        (await ApplyAsync(await UserAsync(), t.Company, new ApplyOnAccountRequest(5_000), key)).Replayed.Should().BeFalse();
        // A replay after the money is gone still answers with the first result.
        await ApplyAsync(finance, t.Company, new ApplyOnAccountRequest());
        (await ApplyAsync(finance, t.Company, new ApplyOnAccountRequest(25_000), key)).Should().BeEquivalentTo(new { outcomes[0].EventId, AppliedMinor = 25_000L, Replayed = true });
        foreach (var bad in new[] { null, "", "short", new string('k', 101), "has space here" })
            await Assert.ThrowsAsync<ValidationException>(() => ApplyAsync(finance, t.Company, new ApplyOnAccountRequest(1), bad ?? string.Empty));
        await Assert.ThrowsAsync<ValidationException>(() => As(finance, s => s.ApplyOnAccountAsync(t.Company, new ApplyOnAccountRequest(1), null)));
        (await LedgerAddsUpAsync(t.Company)).Count(l => l.Kind == CreditLedgerKind.OnAccountApplied).Should().Be(4);
    }

    [PostgresFact]
    public async Task ApplyOnAccount_ForAnUnknownOrPendingCompany_Is404_ButASuspendedOneMayUseItsMoney()
    {
        var finance = await UserAsync();
        foreach (var company in new[] { (await TeamAsync(activate: false)).Company, Guid.NewGuid() })
            await Assert.ThrowsAsync<KeyNotFoundException>(() => ApplyAsync(finance, company, new ApplyOnAccountRequest()));

        var t = await TeamAsync();
        await PaidAsync(t, 10_000);
        var order = await ChargedOrderAsync(t, 30_000, daysAgo: 40);
        await using (var db = fixture.CreateContext()) await new CompanyService(db).AdminSuspendAsync(t.Company);
        (await ApplyAsync(finance, t.Company, new ApplyOnAccountRequest())).Applied.Should().ContainSingle().Which.OrderId.Should().Be(order);
    }

    // ---- CS29 ---------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task ApplyOnAccount_WritesOneAuditRow_WithActorIpAndCorrelationId_AndAnOutboxEvent()
    {
        var t = await TeamAsync();
        await PaidAsync(t, 15_000);
        await ChargedOrderAsync(t, 40_000);
        var finance = await UserAsync();

        var result = await ApplyAsync(finance, t.Company, new ApplyOnAccountRequest(), corr: "corr-audit-apply");
        (await CodeOf(() => ApplyAsync(finance, t.Company, new ApplyOnAccountRequest()))).Should().Be("CREDIT_NOTHING_TO_APPLY", "a refusal is not audited");

        await using var db = fixture.CreateContext();
        var audit = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == "credit.on_account_applied" && a.EntityId == t.Company.ToString());
        audit.Should().BeEquivalentTo(new { ActorId = (Guid?)finance, ActorIp = Ip, CorrelationId = "corr-audit-apply", EntityName = "companies" });
        using (var old = JsonDocument.Parse(audit.OldValuesJson!))
            (old.RootElement.GetProperty("creditUsedMinor").GetInt64(), old.RootElement.GetProperty("creditOnAccountMinor").GetInt64()).Should().Be((40_000, 15_000));
        using (var after = JsonDocument.Parse(audit.NewValuesJson!))
        {
            (after.RootElement.GetProperty("eventId").GetGuid(), after.RootElement.GetProperty("appliedMinor").GetInt64()).Should().Be((result.EventId, 15_000));
            (after.RootElement.GetProperty("creditUsedMinor").GetInt64(), after.RootElement.GetProperty("creditOnAccountMinor").GetInt64()).Should().Be((25_000, 0));
        }
        (await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == result.EventId)).Should()
            .BeEquivalentTo(new { EventType = "credit.on_account_applied", AggregateId = t.Company });
    }

    // ---- CS16: the lock order ---------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task AnApply_DoesNotWaitForACancelOrABounceHoldingTheRowsItNames()
    {
        // A cancel holds its order and a bounce holds its payment (both NO KEY UPDATE) before they queue for the company. The apply
        // takes the company and writes lines naming that order and that payment (FOR KEY SHARE through the foreign keys): it must
        // finish while they still hold their rows, or the two would wait for each other.
        var t = await TeamAsync();
        var payment = await PaidAsync(t, 30_000);
        var order = await ChargedOrderAsync(t, 30_000);
        await using var db = Ctx();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM orders WHERE id = {order} FOR NO KEY UPDATE");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM credit_payments WHERE id = {payment} FOR NO KEY UPDATE");

        var apply = Task.Run(async () => await ApplyAsync(await UserAsync(), t.Company, new ApplyOnAccountRequest()));
        (await Task.WhenAny(apply, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(apply, "the apply does not wait for the cancel or the bounce");
        (await apply).Applied.Should().Equal(new CreditOnAccountApplication(order, payment, null, 30_000));
        await tx.RollbackAsync();
    }

    private async Task<PlacedOrder> PlaceAsync(Guid buyer, Guid company, Guid variant)
    {
        await using var db = fixture.CreateContext();
        var orders = new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), TimeProvider.System,
            ShippingTestKit.DevelopmentQuoter(db), new Infrastructure.Pricing.PriceListResolver(db));
        return await orders.PlaceAsync(new PlaceOrderRequest(buyer, Key(), [new PlaceOrderLine(variant, 1)], null, company, PayOnCredit: true));
    }

    [PostgresFact]
    public async Task ApplyVerifyBounceAndCancel_InParallel_NeverDeadlock_AndTheLedgerAddsUp()
    {
        // One company, all at once: 8 applies (company row; one pair shares a key), 6 verifications and 4 bounces (payment row →
        // company row), 6 cancels (order row → company row) and 4 new credit orders (price lists → company row). No deadlock (40P01),
        // no lost update; an apply may only find the money gone (409); I1, I2 per source, I4 and I5 hold afterwards.
        const long Unit = 10_000;
        var t = await TeamAsync(limitMinor: 1_000 * Unit, buyers: 4);
        var variants = new List<Guid>();
        await using (var db = fixture.CreateContext())
            for (var i = 0; i < 4; i++) variants.Add((await CatalogTestData.SeedVariantAsync(db, 5, priceMinor: Unit)).VariantId);
        var amounts = new Dictionary<Guid, long>();
        var toBounce = new List<Guid>();
        for (var i = 0; i < 4; i++) { var p = await PaidAsync(t, 6 * Unit, i % 2 == 0 ? "cheque" : "rtgs"); toBounce.Add(p); amounts[p] = 6 * Unit; }
        var kept = await PaidAsync(t, 10 * Unit);
        amounts[kept] = 10 * Unit;
        var cancels = new List<Guid>();
        for (var i = 0; i < 6; i++) cancels.Add(await ChargedOrderAsync(t, 3 * Unit, daysAgo: 25 - i));
        await ChargedOrderAsync(t, 50 * Unit, daysAgo: 1);
        var (recorder, verifier, bouncer) = (await UserAsync(), await UserAsync(), await UserAsync());
        var toVerify = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var p = (await As(recorder, s => s.RecordAsync(t.Company, new RecordCreditPaymentRequest("npsb", 2 * Unit, Today, Ref()), Key()))).Payment.Id;
            toVerify.Add(p);
            amounts[p] = 2 * Unit;
        }
        var appliers = new List<Guid>();
        for (var i = 0; i < 7; i++) appliers.Add(await UserAsync());

        using var gate = new ManualResetEventSlim(false);
        var refusals = new System.Collections.Concurrent.ConcurrentBag<string>();
        async Task Apply(Guid who, long? amount, string key)
        {
            gate.Wait();
            try { await ApplyAsync(who, t.Company, new ApplyOnAccountRequest(amount), key); }
            catch (ConflictException ex) when (ex.Code is "CREDIT_ON_ACCOUNT_INSUFFICIENT" or "CREDIT_NOTHING_TO_APPLY") { refusals.Add(ex.Code); }
        }
        var work = new List<Task>();
        work.AddRange(appliers.Select((a, i) => Task.Run(() => Apply(a, i % 2 == 0 ? 4 * Unit : null, Key()))));
        work.Add(Task.Run(() => Apply(appliers[0], 3 * Unit, "shared-key-0001")));
        work.Add(Task.Run(() => Apply(appliers[0], 3 * Unit, "shared-key-0001")));
        work.AddRange(cancels.Select(o => Task.Run(async () => { gate.Wait(); await ReduceAsync(o); })));
        work.AddRange(toVerify.Select(p => Task.Run(async () => { gate.Wait(); await As(verifier, s => s.VerifyAsync(p, new VerifyCreditPaymentRequest(Today))); })));
        work.AddRange(toBounce.Select(p => Task.Run(async () => { gate.Wait(); await BounceAsync(bouncer, p); })));
        work.AddRange(t.Buyers.Select((b, i) => Task.Run(async () => { gate.Wait(); await PlaceAsync(b, t.Company, variants[i]); })));
        gate.Set();
        var all = Task.WhenAll(work);
        (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(90)))).Should().BeSameAs(all, "a deadlock would hang or abort");
        await all;   // any 40P01, lost update or broken chain throws here

        var lines = await LedgerAddsUpAsync(t.Company);
        lines.Count(l => l.Kind == CreditLedgerKind.OnAccountApplied).Should().BeGreaterThan(0, string.Join(",", refusals));
        lines.Count(l => l.IdempotencyKey.Contains("shared-key", StringComparison.Ordinal)).Should().Be(0, "keys carry a hash, never the client's key");
        foreach (var o in cancels)
            lines.Where(l => l.OrderId == o).Sum(l => l.ReceivableDeltaMinor).Should().Be(0, "a cancelled order owes nothing, whatever ran before");
        foreach (var (p, amount) in amounts)
            MoneyOf(lines, p).Should().Be(toBounce.Contains(p) ? 0 : amount, $"I5 for {p}");
        await using (var db = fixture.CreateContext())
            (await db.CreditPayments.CountAsync(p => toBounce.Contains(p.Id) && p.Status == CreditPaymentStatus.Bounced)).Should().Be(4);
    }
}
