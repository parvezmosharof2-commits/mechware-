using FluentAssertions;
using ShopVerse.Application.Approvals;
using ShopVerse.Application.Companies;
using ShopVerse.Application.Orders;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Companies;
using ShopVerse.Infrastructure.Approvals;
using ShopVerse.Infrastructure.Companies;
using ShopVerse.Infrastructure.Inventory;
using ShopVerse.Infrastructure.Orders;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Orders;

/// <summary>Threat model v1, M3: cost-centre assignment, member spend limits and monthly budgets are enforced on orders.</summary>
public class CompanySpendControlTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const long Price = 100_000;   // 105,000 per unit with 5 % VAT on company orders
    private static long _binSeq = 8_000_000_000;
    private static string NextBin() => (Interlocked.Increment(ref _binSeq) + 2_000_000_000_000L).ToString("D13");
    private static string Key() => "key-" + Guid.NewGuid().ToString("N");

    private sealed record Team(Guid Company, Guid Admin, Guid Approver, Guid CostCentre, Guid OtherCostCentre);

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private async Task<T> Orders<T>(Func<OrderService, Task<T>> action, DateTime? now = null)
    {
        await using var db = fixture.CreateContext();
        return await action(new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System),
            now is null ? TimeProvider.System : new FixedClock(now.Value)));
    }

    private async Task<Team> TeamAsync(long? budget)
    {
        var (admin, approver) = (await TestUsers.CreateAsync(fixture), await TestUsers.CreateAsync(fixture));
        await using var db = fixture.CreateContext();
        var companies = new CompanyService(db);
        var company = (await companies.RegisterAsync(admin, new RegisterCompanyRequest("Spend Control Ltd", "TL-SC-1", NextBin(), null, null))).Id;
        var cc = (await companies.AddCostCentreAsync(admin, company, new CostCentreRequest("CC-204", "Dyeing maintenance", budget))).Id;
        var other = (await companies.AddCostCentreAsync(admin, company, new CostCentreRequest("CC-310", "Boiler house", null))).Id;
        await companies.AddMemberAsync(admin, company, new AddMemberRequest(approver, CompanyRoles.Approver, null, null));
        await companies.AdminActivateAsync(company);
        return new Team(company, admin, approver, cc, other);
    }

    private async Task<Guid> BuyerAsync(Team t, long? spendLimit, params Guid[] costCentres)
    {
        var buyer = await TestUsers.CreateAsync(fixture);
        await using var db = fixture.CreateContext();
        await new CompanyService(db).AddMemberAsync(t.Admin, t.Company, new AddMemberRequest(buyer, CompanyRoles.Buyer, spendLimit, costCentres));
        return buyer;
    }

    private async Task AddRuleAsync(Team t, long from)
    {
        await using var db = fixture.CreateContext();
        await new ApprovalService(db, TimeProvider.System).AddRuleAsync(t.Admin, t.Company, new ApprovalRuleRequest("From " + from, from, null, null, [t.Approver]));
    }

    private async Task<Guid> VariantAsync()
    {
        await using var db = fixture.CreateContext();
        return (await CatalogTestData.SeedVariantAsync(db, 100, priceMinor: Price)).VariantId;
    }

    private static PlaceOrderRequest Order(Guid buyer, Team t, Guid variant, int qty, Guid? costCentre) =>
        new(buyer, Key(), [new PlaceOrderLine(variant, qty)], null, t.Company, false, null, null, costCentre);

    [PostgresFact]
    public async Task AnAssignedMember_BuysOnlyForTheirOwnCostCentres()
    {
        var t = await TeamAsync(budget: null);
        var buyer = await BuyerAsync(t, null, t.CostCentre);
        var v = await VariantAsync();

        await FluentActions.Awaiting(() => Orders(s => s.PlaceAsync(Order(buyer, t, v, 1, null)))).Should().ThrowAsync<ValidationException>();
        (await FluentActions.Awaiting(() => Orders(s => s.PlaceAsync(Order(buyer, t, v, 1, t.OtherCostCentre)))).Should().ThrowAsync<ForbiddenException>())
            .Which.Code.Should().Be("COST_CENTRE_NOT_ASSIGNED");
        (await Orders(s => s.PlaceAsync(Order(buyer, t, v, 1, t.CostCentre)))).Status.Should().Be("pending_payment");

        // A member with no assignment may use any of the company's cost centres, or none.
        var free = await BuyerAsync(t, null);
        (await Orders(s => s.PlaceAsync(Order(free, t, v, 1, t.OtherCostCentre)))).Status.Should().Be("pending_payment");
    }

    [PostgresFact]
    public async Task AboveTheSpendLimit_AnOrderNeedsAnApprovalRule_OrIsRefused()
    {
        var t = await TeamAsync(budget: null);
        var buyer = await BuyerAsync(t, spendLimit: 200_000);
        var v = await VariantAsync();

        (await Orders(s => s.PlaceAsync(Order(buyer, t, v, 1, null)))).Status.Should().Be("pending_payment", "105,000 is within the limit");
        (await FluentActions.Awaiting(() => Orders(s => s.PlaceAsync(Order(buyer, t, v, 2, null)))).Should().ThrowAsync<ConflictException>())
            .Which.Code.Should().Be("SPEND_LIMIT_EXCEEDED", "210,000 is above it and nobody could approve it");

        await AddRuleAsync(t, 150_000);
        (await Orders(s => s.PlaceAsync(Order(buyer, t, v, 2, null)))).Status.Should().Be("pending_approval");
    }

    [PostgresFact]
    public async Task TheMonthlyBudget_CountsEveryOrderThatMayStillGoThrough_AndCancelledOnesGiveItBack()
    {
        var t = await TeamAsync(budget: 300_000);
        var (buyer, other) = (await BuyerAsync(t, null), await BuyerAsync(t, null));
        var v = await VariantAsync();

        var first = await Orders(s => s.PlaceAsync(Order(buyer, t, v, 2, t.CostCentre)));     // 210,000
        (await FluentActions.Awaiting(() => Orders(s => s.PlaceAsync(Order(other, t, v, 1, t.CostCentre)))).Should().ThrowAsync<ConflictException>())
            .Which.Code.Should().Be("BUDGET_EXCEEDED", "210,000 + 105,000 is over 300,000, whoever places it");

        await Orders(s => s.CancelAsync(buyer, first.OrderId, "Wrong item"));
        (await Orders(s => s.PlaceAsync(Order(other, t, v, 2, t.CostCentre)))).Status.Should().Be("pending_payment");

        await AddRuleAsync(t, 100_000);
        (await Orders(s => s.PlaceAsync(Order(buyer, t, v, 2, t.CostCentre)))).Status.Should().Be("pending_approval", "over budget goes to an approver when a rule exists");
    }

    [PostgresFact]
    public async Task TwoBuyersAtOnce_CannotBothFitUnderTheBudget()
    {
        var t = await TeamAsync(budget: 300_000);
        var buyers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => BuyerAsync(t, null)));
        var v = await VariantAsync();

        var outcomes = await Task.WhenAll(buyers.Select(b => Task.Run(async () =>
        {
            try { await Orders(s => s.PlaceAsync(Order(b, t, v, 2, t.CostCentre))); return "ok"; }
            catch (ConflictException ex) { return ex.Code; }
        })));

        outcomes.Count(o => o == "ok").Should().Be(1);
        outcomes.Where(o => o != "ok").Should().OnlyContain(o => o == "BUDGET_EXCEEDED");
    }

    [PostgresFact]
    public async Task TheBudgetMonth_IsTheCalendarMonthInDhaka()
    {
        var t = await TeamAsync(budget: 300_000);
        var buyer = await BuyerAsync(t, null);
        var v = await VariantAsync();

        // 31 October 23:30 in Dhaka (17:30 UTC), then 1 November 00:30 in Dhaka (18:30 UTC): different budget months.
        await Orders(s => s.PlaceAsync(Order(buyer, t, v, 2, t.CostCentre)), new DateTime(2026, 10, 31, 17, 30, 0, DateTimeKind.Utc));
        (await Orders(s => s.PlaceAsync(Order(buyer, t, v, 2, t.CostCentre)), new DateTime(2026, 10, 31, 18, 30, 0, DateTimeKind.Utc)))
            .Status.Should().Be("pending_payment");
        (await FluentActions.Awaiting(() => Orders(s => s.PlaceAsync(Order(buyer, t, v, 1, t.CostCentre)), new DateTime(2026, 10, 31, 18, 31, 0, DateTimeKind.Utc)))
            .Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("BUDGET_EXCEEDED");
    }

    [PostgresFact]
    public async Task AnAssignedMember_CannotAskForApprovalAgainstSomeoneElsesCostCentre()
    {
        var t = await TeamAsync(budget: null);
        var buyer = await BuyerAsync(t, null, t.CostCentre);
        await AddRuleAsync(t, 1);
        await using var db = fixture.CreateContext();
        var approvals = new ApprovalService(db, TimeProvider.System);

        (await FluentActions.Awaiting(() => approvals.SubmitAsync(buyer, t.Company, new SubmitApprovalRequest("purchase_request", Guid.NewGuid(), 50_000, t.OtherCostCentre, null)))
            .Should().ThrowAsync<ForbiddenException>()).Which.Code.Should().Be("COST_CENTRE_NOT_ASSIGNED");
        (await approvals.SubmitAsync(buyer, t.Company, new SubmitApprovalRequest("purchase_request", Guid.NewGuid(), 50_000, t.CostCentre, null)))
            .ApprovalRequired.Should().BeTrue();
    }
}
