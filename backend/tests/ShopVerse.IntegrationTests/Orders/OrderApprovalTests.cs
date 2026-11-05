using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Approvals;
using ShopVerse.Application.Companies;
using ShopVerse.Application.Orders;
using ShopVerse.Domain.Approvals;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Companies;
using ShopVerse.Domain.Orders;
using ShopVerse.Infrastructure.Approvals;
using ShopVerse.Infrastructure.Companies;
using ShopVerse.Infrastructure.Inventory;
using ShopVerse.Infrastructure.Orders;
using ShopVerse.Infrastructure.Payments;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Orders;

/// <summary>Threat model v1, H1: a company order that an approval rule covers cannot go ahead until it is approved.</summary>
public class OrderApprovalTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const long Price = 100_000;          // per unit, VAT 5 % added for company orders
    private const long RuleFrom = 200_000;        // orders from 2,000.00 need approval

    private static long _binSeq = 7_000_000_000;
    private static string NextBin() => (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13");
    private static string Key() => "key-" + Guid.NewGuid().ToString("N");

    private sealed record Team(Guid Company, Guid Admin, Guid Buyer, Guid Approver, Guid CostCentre);

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private async Task<T> Orders<T>(Func<OrderService, Task<T>> action, DateTime? now = null)
    {
        await using var db = fixture.CreateContext();
        var clock = now is null ? TimeProvider.System : new FixedClock(now.Value);
        return await action(new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), clock));
    }

    /// <summary>Approvals wired to orders as in the running API: a decision moves the order on at once.</summary>
    private async Task<T> Approvals<T>(Func<ApprovalService, Task<T>> action, bool wiredToOrders = true, DateTime? now = null)
    {
        await using var db = fixture.CreateContext();
        var clock = now is null ? TimeProvider.System : new FixedClock(now.Value);
        var orders = new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), clock);
        return await action(new ApprovalService(db, clock, wiredToOrders ? orders : null));
    }

    private async Task<Team> TeamAsync(long creditLimit = 0)
    {
        var (admin, buyer, approver) = (await TestUsers.CreateAsync(fixture), await TestUsers.CreateAsync(fixture), await TestUsers.CreateAsync(fixture));
        await using var db = fixture.CreateContext();
        var companies = new CompanyService(db);
        var company = (await companies.RegisterAsync(admin, new RegisterCompanyRequest("Approving Buyers Ltd", "TL-OA-1", NextBin(), null, null))).Id;
        await companies.AddMemberAsync(admin, company, new AddMemberRequest(buyer, CompanyRoles.Buyer, null, null));
        await companies.AddMemberAsync(admin, company, new AddMemberRequest(approver, CompanyRoles.Approver, null, null));
        var costCentre = (await companies.AddCostCentreAsync(admin, company, new CostCentreRequest("CC-204", "Dyeing maintenance", null))).Id;
        await companies.AdminActivateAsync(company);
        if (creditLimit > 0) await companies.AdminSetCreditAsync(company, new SetCreditRequest(CreditTerms.Net30, creditLimit));
        await new ApprovalService(db, TimeProvider.System).AddRuleAsync(admin, company,
            new ApprovalRuleRequest("Above 2,000", RuleFrom, null, null, [approver]));
        return new Team(company, admin, buyer, approver, costCentre);
    }

    private async Task<SeededVariant> StockAsync(int onHand = 20)
    {
        await using var db = fixture.CreateContext();
        return await CatalogTestData.SeedVariantAsync(db, onHand, priceMinor: Price);
    }

    private static PlaceOrderRequest Order(Team t, Guid variant, int qty, bool credit = false, Guid? costCentre = null) =>
        new(t.Buyer, Key(), [new PlaceOrderLine(variant, qty)], null, t.Company, credit, null, null, costCentre);

    private async Task<Domain.Orders.Order> LoadAsync(Guid orderId)
    {
        await using var db = fixture.CreateContext();
        return await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
    }

    private async Task<ApprovalRequest?> RequestForAsync(Guid orderId)
    {
        await using var db = fixture.CreateContext();
        return await db.ApprovalRequests.AsNoTracking().SingleOrDefaultAsync(r => r.SubjectType == "order" && r.SubjectId == orderId);
    }

    private async Task<(int Reserved, int OnHand)> StockOf(Guid variant)
    {
        await using var db = fixture.CreateContext();
        var item = await db.InventoryItems.AsNoTracking().SingleAsync(i => i.VariantId == variant);
        return (item.Reserved, item.OnHand);
    }

    private async Task<long> CreditUsedAsync(Guid company)
    {
        await using var db = fixture.CreateContext();
        return (await db.Companies.AsNoTracking().SingleAsync(c => c.Id == company)).CreditUsedMinor;
    }

    [PostgresFact]
    public async Task AnOrderTheRuleCovers_WaitsForApproval_ForTheServersOwnTotal_AndCannotBePaid()
    {
        var t = await TeamAsync();
        var v = await StockAsync();

        var small = await Orders(s => s.PlaceAsync(Order(t, v.VariantId, 1)));
        small.Status.Should().Be("pending_payment", "1,050.00 is below the rule");
        (await RequestForAsync(small.OrderId)).Should().BeNull();

        var big = await Orders(s => s.PlaceAsync(Order(t, v.VariantId, 3, costCentre: t.CostCentre)));
        big.Status.Should().Be("pending_approval");
        var request = (await RequestForAsync(big.OrderId))!;
        request.AmountMinor.Should().Be(big.GrandTotalMinor, "the approver sees the order's real total");
        request.CostCentreId.Should().Be(t.CostCentre);
        request.Status.Should().Be(ApprovalStatus.Pending);
        (await StockOf(v.VariantId)).Reserved.Should().Be(4, "an order waiting for approval still holds its stock");

        await using var db = fixture.CreateContext();
        var payments = new PaymentService(db, [new MockPaymentProvider()], new InventoryService(db, new NoopLockProvider(), TimeProvider.System), TimeProvider.System);
        (await FluentActions.Awaiting(() => payments.StartAsync(t.Buyer, big.OrderId, "mock")).Should().ThrowAsync<ConflictException>())
            .Which.Code.Should().Be("ORDER_NOT_PAYABLE", "nobody can pay their way past the approval");
    }

    [PostgresFact]
    public async Task Approving_MakesTheOrderPayable_AndTheTimeToPayStartsThen()
    {
        var t = await TeamAsync();
        var v = await StockAsync();
        var placedAt = DateTime.UtcNow;
        var placed = await Orders(s => s.PlaceAsync(Order(t, v.VariantId, 3)), placedAt);
        var request = (await RequestForAsync(placed.OrderId))!;

        var approvedAt = placedAt.AddHours(5);
        await Approvals(s => s.DecideAsync(t.Approver, t.Company, request.Id, new DecisionRequest(true, "Needed for line 3")), now: approvedAt);

        var order = await LoadAsync(placed.OrderId);
        order.Status.Should().Be(OrderStatus.PendingPayment);
        order.PaymentDueAtUtc.Should().BeCloseTo(approvedAt + OrderService.UnpaidOrderLifetime, TimeSpan.FromSeconds(1));

        // Five hours after placing, the order is not "30 minutes unpaid": its clock started at the approval.
        await Orders(s => s.ExpireUnpaidAsync(approvedAt.AddMinutes(10)), approvedAt.AddMinutes(10));
        (await LoadAsync(placed.OrderId)).Status.Should().Be(OrderStatus.PendingPayment);
        await Orders(s => s.ExpireUnpaidAsync(approvedAt.AddMinutes(31)), approvedAt.AddMinutes(31));
        (await LoadAsync(placed.OrderId)).Status.Should().Be(OrderStatus.Cancelled);
        (await StockOf(v.VariantId)).Reserved.Should().Be(0);
    }

    [PostgresFact]
    public async Task Rejecting_CancelsTheOrder_AndFreesItsStock()
    {
        var t = await TeamAsync();
        var v = await StockAsync();
        var placed = await Orders(s => s.PlaceAsync(Order(t, v.VariantId, 3)));
        var request = (await RequestForAsync(placed.OrderId))!;

        await Approvals(s => s.DecideAsync(t.Approver, t.Company, request.Id, new DecisionRequest(false, "Over budget this month")));

        (await LoadAsync(placed.OrderId)).Status.Should().Be(OrderStatus.Cancelled);
        (await StockOf(v.VariantId)).Reserved.Should().Be(0);
    }

    [PostgresFact]
    public async Task OnCredit_TheCreditIsHeldOnlyWhenApproved()
    {
        var t = await TeamAsync(creditLimit: 1_000_000);
        var v = await StockAsync();
        var placed = await Orders(s => s.PlaceAsync(Order(t, v.VariantId, 3, credit: true)));

        placed.Status.Should().Be("pending_approval");
        (await CreditUsedAsync(t.Company)).Should().Be(0, "an order nobody approved yet uses no credit");

        var request = (await RequestForAsync(placed.OrderId))!;
        await Approvals(s => s.DecideAsync(t.Approver, t.Company, request.Id, new DecisionRequest(true, null)));

        (await LoadAsync(placed.OrderId)).Status.Should().Be(OrderStatus.Confirmed);
        (await CreditUsedAsync(t.Company)).Should().Be(placed.GrandTotalMinor);
        (await StockOf(v.VariantId)).Should().Be((0, 17), "a confirmed order turns its hold into a deduction");
    }

    [PostgresFact]
    public async Task ApprovedOnCredit_ButTheCreditWasUsedUpMeanwhile_TheOrderIsCancelledNotOverdrawn()
    {
        var t = await TeamAsync(creditLimit: 400_000);
        var v = await StockAsync();
        var first = await Orders(s => s.PlaceAsync(Order(t, v.VariantId, 3, credit: true)));   // 315,000, waits
        var second = await Orders(s => s.PlaceAsync(Order(t, v.VariantId, 3, credit: true)));  // 315,000, waits

        foreach (var placed in new[] { first, second })
        {
            var request = (await RequestForAsync(placed.OrderId))!;
            await Approvals(s => s.DecideAsync(t.Approver, t.Company, request.Id, new DecisionRequest(true, null)));
        }

        (await LoadAsync(first.OrderId)).Status.Should().Be(OrderStatus.Confirmed);
        (await LoadAsync(second.OrderId)).Status.Should().Be(OrderStatus.Cancelled, "the credit no longer covers it");
        (await CreditUsedAsync(t.Company)).Should().Be(first.GrandTotalMinor);
        (await StockOf(v.VariantId)).Should().Be((0, 17));
    }

    [PostgresFact]
    public async Task ADecisionThatWasNotAppliedAtOnce_IsAppliedByTheSweep()
    {
        var t = await TeamAsync();
        var v = await StockAsync();
        var placed = await Orders(s => s.PlaceAsync(Order(t, v.VariantId, 3)));
        var request = (await RequestForAsync(placed.OrderId))!;

        // The process died right after the decision was saved: the order did not move.
        await Approvals(s => s.DecideAsync(t.Approver, t.Company, request.Id, new DecisionRequest(true, null)), wiredToOrders: false);
        (await LoadAsync(placed.OrderId)).Status.Should().Be(OrderStatus.PendingApproval);

        await Orders(s => s.SettleApprovalsAsync(DateTime.UtcNow));
        (await LoadAsync(placed.OrderId)).Status.Should().Be(OrderStatus.PendingPayment);
        (await Orders(s => s.SettleApprovalsAsync(DateTime.UtcNow))).Should().Be(0, "applying twice changes nothing");
    }

    [PostgresFact]
    public async Task AnOrderNobodyDecidesOn_LapsesAfter24Hours_AndLeavesTheInbox()
    {
        var t = await TeamAsync();
        var v = await StockAsync();
        var placed = await Orders(s => s.PlaceAsync(Order(t, v.VariantId, 3)));

        await Orders(s => s.SettleApprovalsAsync(DateTime.UtcNow.AddHours(23)));
        (await LoadAsync(placed.OrderId)).Status.Should().Be(OrderStatus.PendingApproval);

        await Orders(s => s.SettleApprovalsAsync(DateTime.UtcNow + OrderService.ApprovalHoldLifetime + TimeSpan.FromMinutes(1)));
        (await LoadAsync(placed.OrderId)).Status.Should().Be(OrderStatus.Cancelled);
        (await RequestForAsync(placed.OrderId))!.Status.Should().Be(ApprovalStatus.Cancelled);
        (await StockOf(v.VariantId)).Reserved.Should().Be(0);
        (await Approvals(s => s.ListInboxAsync(t.Approver, t.Company))).Should().BeEmpty();
    }

    [PostgresFact]
    public async Task AClient_CannotCreateAnOrderApprovalWithItsOwnAmount_OrUseAnotherCompanysCostCentre()
    {
        var t = await TeamAsync();
        var other = await TeamAsync();
        var v = await StockAsync();

        await FluentActions.Awaiting(() => Approvals(s => s.SubmitAsync(t.Buyer, t.Company, new SubmitApprovalRequest("order", Guid.NewGuid(), 1, null, null))))
            .Should().ThrowAsync<ValidationException>();
        await FluentActions.Awaiting(() => Orders(s => s.PlaceAsync(Order(t, v.VariantId, 3, costCentre: other.CostCentre))))
            .Should().ThrowAsync<KeyNotFoundException>();
        (await StockOf(v.VariantId)).Reserved.Should().Be(0, "a refused order holds nothing");
    }

    [PostgresFact]
    public async Task OrdersWaitingForApproval_CountTowardsTheOpenOrderCap()
    {
        var t = await TeamAsync();
        var v = await StockAsync(onHand: 50);
        for (var i = 0; i < PlaceOrderRequest.MaxOpenUnpaidOrdersPerBuyer; i++)
            (await Orders(s => s.PlaceAsync(Order(t, v.VariantId, 3)))).Status.Should().Be("pending_approval");

        (await FluentActions.Awaiting(() => Orders(s => s.PlaceAsync(Order(t, v.VariantId, 3)))).Should().ThrowAsync<ConflictException>())
            .Which.Code.Should().Be("TOO_MANY_UNPAID_ORDERS", "each one holds stock for up to a day");
    }

    [PostgresFact]
    public async Task TheBuyerCancellingAWaitingOrder_TakesItsRequestOutOfTheApproversInbox()
    {
        var t = await TeamAsync();
        var v = await StockAsync();
        var placed = await Orders(s => s.PlaceAsync(Order(t, v.VariantId, 3)));
        (await Approvals(s => s.ListInboxAsync(t.Approver, t.Company))).Should().ContainSingle();

        await Orders(s => s.CancelAsync(t.Buyer, placed.OrderId, "Ordered the wrong model"));

        (await RequestForAsync(placed.OrderId))!.Status.Should().Be(ApprovalStatus.Cancelled);
        (await Approvals(s => s.ListInboxAsync(t.Approver, t.Company))).Should().BeEmpty();
        (await StockOf(v.VariantId)).Reserved.Should().Be(0);
    }
}
