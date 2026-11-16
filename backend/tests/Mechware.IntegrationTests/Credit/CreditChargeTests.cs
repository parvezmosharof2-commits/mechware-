using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Approvals;
using Mechware.Application.Companies;
using Mechware.Application.Orders;
using Mechware.Domain.Approvals;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Domain.Orders;
using Mechware.Infrastructure.Approvals;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Orders;
using Mechware.IntegrationTests.Orders;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Credit;

/// <summary>
/// Threat model §১২ CS15 (charges half), CS18, CS26: a credit order posts exactly one charge, in the confirmation's own
/// transaction and under the company row lock, at the two places an order is confirmed on credit (placement, approval).
/// </summary>
public class CreditChargeTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const long Price = 100_000;           // per unit, VAT-inclusive: a company order of n units totals n × 1,000.00
    private const long RuleFrom = 200_000;        // the approval rule covers orders from 2,000.00

    private static long _binSeq = 3_300_000_000;
    private static string NextBin() => (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13");
    private static string Key() => "key-" + Guid.NewGuid().ToString("N");

    private sealed record Team(Guid Company, Guid Admin, IReadOnlyList<Guid> Buyers, Guid Approver)
    {
        public Guid Buyer => Buyers[0];
    }

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private OrderService NewOrders(Infrastructure.Persistence.MechwareDbContext db, DateTime? now) =>
        new(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), now is null ? TimeProvider.System : new FixedClock(now.Value),
            ShippingTestKit.DevelopmentQuoter(db), new Infrastructure.Pricing.PriceListResolver(db));

    private async Task<T> Orders<T>(Func<OrderService, Task<T>> action, DateTime? now = null)
    {
        await using var db = fixture.CreateContext();
        return await action(NewOrders(db, now));
    }

    private async Task<T> Approvals<T>(Func<ApprovalService, Task<T>> action, bool wiredToOrders = true)
    {
        await using var db = fixture.CreateContext();
        return await action(new ApprovalService(db, TimeProvider.System, wiredToOrders ? NewOrders(db, null) : null));
    }

    private async Task<Team> TeamAsync(long limitMinor, CreditTerms terms = CreditTerms.Net30, bool approvalRule = false, int buyers = 1)
    {
        var admin = await TestUsers.CreateAsync(fixture);
        var approver = await TestUsers.CreateAsync(fixture);
        var buyerIds = new List<Guid>();
        for (var i = 0; i < buyers; i++) buyerIds.Add(await TestUsers.CreateAsync(fixture));

        await using var db = fixture.CreateContext();
        var companies = new CompanyService(db);
        var company = (await companies.RegisterAsync(admin, new RegisterCompanyRequest("Credit Buyers Ltd", "TL-CC-1", NextBin(), null, null))).Id;
        foreach (var buyer in buyerIds)
            await companies.AddAcceptedMemberAsync(admin, company, new AddMemberRequest(buyer, CompanyRoles.Buyer, null, null));
        await companies.AddAcceptedMemberAsync(admin, company, new AddMemberRequest(approver, CompanyRoles.Approver, null, null));
        await companies.AdminActivateAsync(company);
        await companies.AdminSetCreditAsync(company, new SetCreditRequest(terms, limitMinor));
        if (approvalRule)
            await new ApprovalService(db, TimeProvider.System).AddRuleAsync(admin, company, new ApprovalRuleRequest("From 2,000", RuleFrom, null, null, [approver]));
        return new Team(company, admin, buyerIds, approver);
    }

    private async Task<Guid> VariantAsync(int onHand = 50)
    {
        await using var db = fixture.CreateContext();
        return (await CatalogTestData.SeedVariantAsync(db, onHand, priceMinor: Price)).VariantId;
    }

    private static PlaceOrderRequest OnCredit(Guid buyer, Guid company, Guid variant, int qty, string? key = null) =>
        new(buyer, key ?? Key(), [new PlaceOrderLine(variant, qty)], null, company, PayOnCredit: true);

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

    private async Task<OrderStatus> StatusOf(Guid order)
    {
        await using var db = fixture.CreateContext();
        return await db.Orders.AsNoTracking().Where(o => o.Id == order).Select(o => o.Status).SingleAsync();
    }

    private async Task<Guid> RequestOf(Guid order)
    {
        await using var db = fixture.CreateContext();
        return await db.ApprovalRequests.AsNoTracking().Where(r => r.SubjectType == "order" && r.SubjectId == order).Select(r => r.Id).SingleAsync();
    }

    [PostgresFact]
    public async Task ACreditOrder_PostsExactlyOneCharge_AtConfirmation()
    {
        var t = await TeamAsync(limitMinor: 1_000_000);
        var v = await VariantAsync();
        var at = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);

        var first = await Orders(s => s.PlaceAsync(OnCredit(t.Buyer, t.Company, v, 2)), at);
        var key = Key();
        var second = await Orders(s => s.PlaceAsync(OnCredit(t.Buyer, t.Company, v, 1, key)), at.AddMinutes(5));
        var replay = await Orders(s => s.PlaceAsync(OnCredit(t.Buyer, t.Company, v, 1, key)), at.AddMinutes(6));   // the client retries

        replay.Replayed.Should().BeTrue();
        var lines = await LinesAsync(t.Company);
        lines.Should().HaveCount(2, "one charge per confirmed order, none for the replayed request");
        lines[0].Should().BeEquivalentTo(new
        {
            Seq = 1L,
            Kind = CreditLedgerKind.Charge,
            OrderId = (Guid?)first.OrderId,
            ReceivableDeltaMinor = 200_000L,
            ReceivableAfterMinor = 200_000L,
            OnAccountDeltaMinor = 0L,
            OnAccountAfterMinor = 0L,
            OccurredOn = new DateOnly(2026, 11, 16),
            DueOn = (DateOnly?)new DateOnly(2026, 12, 16),
            ActorUserId = (Guid?)t.Buyer,
            IdempotencyKey = CreditLedgerKeys.Charge(first.OrderId),
            Currency = "BDT"
        });
        (lines[1].OrderId, lines[1].Seq, lines[1].ReceivableAfterMinor).Should().Be((second.OrderId, 2L, 300_000L));

        var company = await CompanyOf(t.Company);
        (company.CreditUsedMinor, company.CreditOnAccountMinor, company.CreditLedgerSeq).Should().Be((300_000, 0, 2));
        lines.Sum(l => l.ReceivableDeltaMinor).Should().Be(company.CreditUsedMinor);
    }

    [PostgresFact]
    public async Task ACharge_IsDueOnTheDhakaDateOfConfirmation_PlusTheCompanysTermsDays()
    {
        // CS26: 17:59:59 UTC is 23:59:59 in Dhaka (the 16th); 18:00 UTC is already the 17th in Dhaka.
        var t = await TeamAsync(limitMinor: 1_000_000, CreditTerms.Net15);
        var v = await VariantAsync();
        await Orders(s => s.PlaceAsync(OnCredit(t.Buyer, t.Company, v, 1)), new DateTime(2026, 11, 16, 17, 59, 59, DateTimeKind.Utc));
        await Orders(s => s.PlaceAsync(OnCredit(t.Buyer, t.Company, v, 1)), new DateTime(2026, 11, 16, 18, 0, 0, DateTimeKind.Utc));

        // Finance moves the company to Net-30: the charges already posted keep their dates, the next one gets 30 days.
        await using (var db = fixture.CreateContext())
            await new CompanyService(db).AdminSetCreditAsync(t.Company, new SetCreditRequest(CreditTerms.Net30, 1_000_000));
        await Orders(s => s.PlaceAsync(OnCredit(t.Buyer, t.Company, v, 1)), new DateTime(2026, 11, 16, 18, 0, 0, DateTimeKind.Utc));

        (await LinesAsync(t.Company)).Select(l => (l.OccurredOn, l.DueOn)).Should().Equal(
            (new DateOnly(2026, 11, 16), (DateOnly?)new DateOnly(2026, 12, 1)),
            (new DateOnly(2026, 11, 17), new DateOnly(2026, 12, 2)),
            (new DateOnly(2026, 11, 17), new DateOnly(2026, 12, 17)));
    }

    [PostgresFact]
    public async Task ReplayingAnApproval_DoesNotChargeTwice()
    {
        var t = await TeamAsync(limitMinor: 1_000_000, approvalRule: true);
        var v = await VariantAsync();
        var placed = await Orders(s => s.PlaceAsync(OnCredit(t.Buyer, t.Company, v, 3)));
        placed.Status.Should().Be("pending_approval");

        // The decision is saved but the process died before applying it; then a retry, two sweeps and the API all apply it at once.
        var request = await RequestOf(placed.OrderId);
        await Approvals(s => s.DecideAsync(t.Approver, t.Company, request, new DecisionRequest(true, null)), wiredToOrders: false);
        using var gate = new ManualResetEventSlim(false);
        var racers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            gate.Wait();
            return await Orders(s => s.ApplyApprovalAsync(placed.OrderId));
        })).ToList();
        gate.Set();
        var applied = await Task.WhenAll(racers);
        await Orders(s => s.SettleApprovalsAsync(DateTime.UtcNow));
        (await Orders(s => s.ApplyApprovalAsync(placed.OrderId))).Should().BeFalse("an applied approval is not applied again");

        applied.Count(x => x).Should().Be(1);
        (await StatusOf(placed.OrderId)).Should().Be(OrderStatus.Confirmed);
        var lines = await LinesAsync(t.Company);
        lines.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            OrderId = (Guid?)placed.OrderId,
            ReceivableDeltaMinor = placed.GrandTotalMinor,
            ActorUserId = (Guid?)null,
            Seq = 1L
        }, "the approval path charges once, as the system");
        (await CompanyOf(t.Company)).CreditUsedMinor.Should().Be(placed.GrandTotalMinor);
    }

    [PostgresFact]
    public async Task APendingApprovalCreditOrder_ChargesNothing()
    {
        var t = await TeamAsync(limitMinor: 400_000, approvalRule: true);
        var v = await VariantAsync();
        var waiting = await Orders(s => s.PlaceAsync(OnCredit(t.Buyer, t.Company, v, 3)));     // 300,000
        var rejected = await Orders(s => s.PlaceAsync(OnCredit(t.Buyer, t.Company, v, 2)));    // 200,000
        var withdrawn = await Orders(s => s.PlaceAsync(OnCredit(t.Buyer, t.Company, v, 2)));
        var tooLate = await Orders(s => s.PlaceAsync(OnCredit(t.Buyer, t.Company, v, 2)));
        (await LinesAsync(t.Company)).Should().BeEmpty("an order nobody approved yet holds no credit");

        var (rejectedRequest, waitingRequest, tooLateRequest) = (await RequestOf(rejected.OrderId), await RequestOf(waiting.OrderId), await RequestOf(tooLate.OrderId));
        await Approvals(s => s.DecideAsync(t.Approver, t.Company, rejectedRequest, new DecisionRequest(false, "Not this month")));
        await Orders(s => s.CancelAsync(t.Buyer, withdrawn.OrderId, "Ordered the wrong model"));
        await Approvals(s => s.DecideAsync(t.Approver, t.Company, waitingRequest, new DecisionRequest(true, null)));
        // Approved, but the 300,000 just charged leaves 100,000 of the 400,000 limit: cancelled, not charged.
        await Approvals(s => s.DecideAsync(t.Approver, t.Company, tooLateRequest, new DecisionRequest(true, null)));

        (await StatusOf(rejected.OrderId), await StatusOf(withdrawn.OrderId), await StatusOf(tooLate.OrderId))
            .Should().Be((OrderStatus.Cancelled, OrderStatus.Cancelled, OrderStatus.Cancelled));
        (await StatusOf(waiting.OrderId)).Should().Be(OrderStatus.Confirmed);
        var lines = await LinesAsync(t.Company);
        lines.Should().ContainSingle().Which.OrderId.Should().Be(waiting.OrderId, "only the confirmed order is charged");
        (await CompanyOf(t.Company)).CreditUsedMinor.Should().Be(300_000);
    }

    [PostgresFact]
    public async Task ParallelApprovalsOfOneCompany_NeverExceedTheLimit_AndTheLedgerAddsUp()
    {
        // Ten approved 200,000 orders confirmed at once (sweeps, API) against a 1,000,000 limit. Each order has its own row lock,
        // so only the company row lock orders the charges: exactly 5 are charged, 5 are cancelled, none fails otherwise.
        var t = await TeamAsync(limitMinor: 1_000_000, approvalRule: true);
        var v = await VariantAsync(onHand: 40);
        var orders = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            var placed = await Orders(s => s.PlaceAsync(OnCredit(t.Buyer, t.Company, v, 2)));
            var request = await RequestOf(placed.OrderId);
            await Approvals(s => s.DecideAsync(t.Approver, t.Company, request, new DecisionRequest(true, null)), wiredToOrders: false);
            orders.Add(placed.OrderId);
        }

        using var gate = new ManualResetEventSlim(false);
        var racers = orders.Select(id => Task.Run(async () =>
        {
            gate.Wait();
            return await Orders(s => s.ApplyApprovalAsync(id));
        })).ToList();
        gate.Set();
        (await Task.WhenAll(racers)).Should().OnlyContain(applied => applied);

        var statuses = new List<OrderStatus>();
        foreach (var id in orders) statuses.Add(await StatusOf(id));
        statuses.Count(s => s == OrderStatus.Confirmed).Should().Be(5);
        statuses.Count(s => s == OrderStatus.Cancelled).Should().Be(5, "the credit no longer covered them");
        var lines = await LinesAsync(t.Company);
        lines.Select(l => l.Seq).Should().Equal(Enumerable.Range(1, 5).Select(i => (long)i));
        (await CompanyOf(t.Company)).CreditUsedMinor.Should().Be(1_000_000).And.Be(lines.Sum(l => l.ReceivableDeltaMinor));
    }

    [PostgresFact]
    public async Task ParallelCreditOrdersOfManyBuyers_NeverExceedTheLimit_AndTheLedgerAddsUp()
    {
        // 24 buyers of one company, one 100,000 order each, against a 1,000,000 limit. Different buyers do not share the per-buyer
        // lock and each has its own variant, so only the company row lock orders them: exactly 10 must win, none may fail otherwise.
        var t = await TeamAsync(limitMinor: 1_000_000, buyers: 24);
        var variants = new List<Guid>();
        for (var i = 0; i < t.Buyers.Count; i++) variants.Add(await VariantAsync(onHand: 5));

        using var gate = new ManualResetEventSlim(false);
        var tasks = t.Buyers.Select((buyer, i) => Task.Run(async () =>
        {
            gate.Wait();
            try { return (await Orders(s => s.PlaceAsync(OnCredit(buyer, t.Company, variants[i], 1)))).OrderId.ToString(); }
            catch (ConflictException ex) { return ex.Code; }
        })).ToList();
        gate.Set();
        var outcomes = await Task.WhenAll(tasks);

        var won = outcomes.Where(o => Guid.TryParse(o, out _)).Select(Guid.Parse).ToHashSet();
        won.Should().HaveCount(10);
        outcomes.Where(o => !Guid.TryParse(o, out _)).Should().OnlyContain(o => o == "CREDIT_LIMIT_EXCEEDED");
        var lines = await LinesAsync(t.Company);
        lines.Select(l => l.Seq).Should().Equal(Enumerable.Range(1, 10).Select(i => (long)i));
        lines.Select(l => l.OrderId!.Value).Should().BeEquivalentTo(won);
        var company = await CompanyOf(t.Company);
        company.CreditUsedMinor.Should().Be(1_000_000).And.Be(lines.Sum(l => l.ReceivableDeltaMinor)).And.Be(lines[^1].ReceivableAfterMinor);
    }
}
