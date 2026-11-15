using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Orders;
using Mechware.Application.Payments;
using Mechware.Domain.Inventory;
using Mechware.Domain.Orders;
using Mechware.Domain.Payments;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Orders;
using Mechware.Infrastructure.Payments;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Orders;

/// <summary>
/// E8.3 time travel: orders are confirmed through the real payment path with a clock set to a chosen instant, then the overdue
/// query is asked "as of" other instants. Same city = the ship-to district is the seller's dispatch warehouse district.
/// </summary>
public class OrderDeliveryDeadlineTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTime T0 = new(2030, 1, 10, 10, 0, 0, DateTimeKind.Utc);
    private readonly MockPaymentProvider _mock = new();

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private static string Key() => "key-" + Guid.NewGuid().ToString("N");

    /// <param name="dispatchDistrict">Text of the district of the seller's own warehouse; null = the seller has none (the hub of the test data, Dhaka text, is used).</param>
    /// <param name="shipToDistrict">District name of the buyer's address.</param>
    private async Task<(Guid OrderId, Guid Buyer, SeededVariant Variant)> ConfirmedAsync(
        string? dispatchDistrict, string shipToDistrict, DateTime confirmedAt, DeliveryOptions? options = null)
    {
        var buyer = await TestUsers.CreateAsync(fixture);
        await using var seedDb = fixture.CreateContext();
        var v = await CatalogTestData.SeedVariantAsync(seedDb, onHand: 10, priceMinor: 100_000);
        if (dispatchDistrict is not null)
        {
            var sellerId = await seedDb.Products.AsNoTracking().Where(p => p.Id == v.ProductId).Select(p => p.SellerId).SingleAsync();
            seedDb.Warehouses.Add(new Warehouse(Guid.NewGuid(), sellerId, "Seller depot", "SD-" + Guid.NewGuid().ToString("N")[..8], "Dhaka", dispatchDistrict, "Depot road"));
            await seedDb.SaveChangesAsync();
        }

        var address = ShippingTestKit.AddressIn(buyer, shipToDistrict);
        seedDb.Addresses.Add(address);
        await seedDb.SaveChangesAsync();

        await using var db = fixture.CreateContext();
        var inv = new InventoryService(db, new NoopLockProvider(), TimeProvider.System);
        var orders = new OrderService(db, inv, TimeProvider.System, ShippingTestKit.DevelopmentQuoter(db), new Mechware.Infrastructure.Pricing.PriceListResolver(db));
        var placed = await orders.PlaceAsync(new PlaceOrderRequest(buyer, Key(), [new PlaceOrderLine(v.VariantId, 1)], address.Id));
        var payments = new PaymentService(db, [_mock], inv, new FixedClock(confirmedAt), ShippingTestKit.Deadlines(db, options));
        var session = await payments.StartAsync(buyer, placed.OrderId, "mock");
        var providerRef = session.RedirectUrl[(session.RedirectUrl.LastIndexOf('/') + 1)..];

        _mock.SimulatePayment(providerRef);
        var body = $$"""{ "id": "evt_{{Guid.NewGuid():N}}", "providerRef": "{{providerRef}}" }""";
        var ts = new DateTimeOffset(confirmedAt).ToUnixTimeSeconds();
        var outcome = await payments.HandleWebhookAsync("mock", WebhookVerifier.Sign(MockPaymentProvider.DefaultSecret, ts, body), ts.ToString(), body);
        outcome.Should().Be(WebhookOutcome.Confirmed);
        return (placed.OrderId, buyer, v);
    }

    private async Task<(string City, string Other)> TwoDistrictsAsync()
    {
        await using var db = fixture.CreateContext();
        var a = await ShippingTestKit.DistrictAsync(db);
        var b = await ShippingTestKit.DistrictAsync(db);
        return (a.NameEn, b.NameEn);
    }

    private async Task<Order> LoadAsync(Guid id)
    {
        await using var db = fixture.CreateContext();
        return await db.Orders.AsNoTracking().SingleAsync(o => o.Id == id);
    }

    private async Task<IReadOnlyList<OverdueOrderDto>> OverdueAsync(DateTime asOf, int page = 1, int pageSize = 50)
    {
        await using var db = fixture.CreateContext();
        return (await new OrderDeadlineService(db).ListOverdueAsync(asOf, page, pageSize)).Items;
    }

    private async Task MoveAsync(Guid orderId, params (OrderStatus To, string? Reason)[] steps)
    {
        await using var db = fixture.CreateContext();
        var order = await db.Orders.Include(o => o.SubOrders).SingleAsync(o => o.Id == orderId);
        foreach (var (to, reason) in steps) order.Transition(to, null, reason, T0.AddHours(1));
        await db.SaveChangesAsync();
    }

    [PostgresFact]
    public async Task SameCity_gets_5_days_OtherCity_10_days_and_the_handover_deadline_is_48_hours()
    {
        var (city, other) = await TwoDistrictsAsync();
        var same = await LoadAsync((await ConfirmedAsync(city, city, T0)).OrderId);
        var far = await LoadAsync((await ConfirmedAsync(city, other, T0)).OrderId);

        same.ConfirmedAtUtc.Should().Be(T0);
        same.DeadlineSameCity.Should().BeTrue();
        same.LegalDeliveryDeadlineUtc.Should().Be(T0.AddDays(5));
        same.CourierHandoverDeadlineUtc.Should().Be(T0.AddHours(48));
        far.DeadlineSameCity.Should().BeFalse();
        far.LegalDeliveryDeadlineUtc.Should().Be(T0.AddDays(10));
        far.CourierHandoverDeadlineUtc.Should().Be(T0.AddHours(48));
    }

    [PostgresFact]
    public async Task An_unknown_district_fails_strict_with_5_days()
    {
        var (city, _) = await TwoDistrictsAsync();
        // The buyer's district is not in the location list: no district id, so the short period.
        var unknownDestination = await LoadAsync((await ConfirmedAsync(city, "Nowhereville " + Guid.NewGuid().ToString("N")[..6], T0)).OrderId);
        // The seller's depot district is not in the location list either.
        var unknownDispatch = await LoadAsync((await ConfirmedAsync("Nowhere " + Guid.NewGuid().ToString("N")[..6], city, T0)).OrderId);

        foreach (var o in new[] { unknownDestination, unknownDispatch })
        {
            o.DeadlineSameCity.Should().BeNull();
            o.LegalDeliveryDeadlineUtc.Should().Be(T0.AddDays(5), "when the district is unknown the strict (short) period applies");
        }
    }

    [PostgresFact]
    public async Task The_exact_boundary_instants_of_the_overdue_query()
    {
        var (city, _) = await TwoDistrictsAsync();
        var id = (await ConfirmedAsync(city, city, T0)).OrderId;
        var legal = T0.AddDays(5);
        var handover = T0.AddHours(48);

        // Handover (still only confirmed): on time at the instant, late one second after.
        (await OverdueAsync(handover.AddSeconds(-1))).Should().NotContain(o => o.OrderId == id);
        (await OverdueAsync(handover)).Should().NotContain(o => o.OrderId == id, "at the deadline instant the order is still on time");
        var late = (await OverdueAsync(handover.AddSeconds(1))).Single(o => o.OrderId == id);
        late.HandoverOverdue.Should().BeTrue();
        late.DeliveryOverdue.Should().BeFalse();

        // Delivery: the same rule at 5 days, and the hours reported are the larger of the two lateness values.
        (await OverdueAsync(legal)).Single(o => o.OrderId == id).DeliveryOverdue.Should().BeFalse();
        var afterLegal = (await OverdueAsync(legal.AddSeconds(1))).Single(o => o.OrderId == id);
        afterLegal.DeliveryOverdue.Should().BeTrue();
        (await OverdueAsync(legal.AddHours(3))).Single(o => o.OrderId == id).HoursOverdue.Should().BeApproximately(3 + 72, 0.01, "the handover is 72 h late, the delivery 3 h; the larger is shown");
    }

    [PostgresFact]
    public async Task A_shipped_order_is_no_longer_late_for_handover_but_still_for_delivery()
    {
        var (city, _) = await TwoDistrictsAsync();
        var id = (await ConfirmedAsync(city, city, T0)).OrderId;
        await MoveAsync(id, (OrderStatus.Processing, null), (OrderStatus.Packed, null), (OrderStatus.Shipped, null));

        (await OverdueAsync(T0.AddDays(3))).Should().NotContain(o => o.OrderId == id, "shipped before the legal deadline: handover no longer counts");
        var late = (await OverdueAsync(T0.AddDays(6))).Single(o => o.OrderId == id);
        late.DeliveryOverdue.Should().BeTrue();
        late.HandoverOverdue.Should().BeFalse();
    }

    [PostgresFact]
    public async Task Delivered_cancelled_and_refunded_orders_are_never_listed_overdue()
    {
        var (city, _) = await TwoDistrictsAsync();
        var delivered = (await ConfirmedAsync(city, city, T0)).OrderId;
        var cancelled = (await ConfirmedAsync(city, city, T0)).OrderId;
        var returned = (await ConfirmedAsync(city, city, T0)).OrderId;
        await MoveAsync(delivered, (OrderStatus.Processing, null), (OrderStatus.Packed, null), (OrderStatus.Shipped, null), (OrderStatus.OutForDelivery, null), (OrderStatus.Delivered, null));
        await MoveAsync(cancelled, (OrderStatus.Cancelled, "Buyer asked"));
        await MoveAsync(returned, (OrderStatus.Processing, null), (OrderStatus.Packed, null), (OrderStatus.Shipped, null), (OrderStatus.DeliveryFailed, "Nobody home"), (OrderStatus.Returned, null), (OrderStatus.Refunded, null));

        var listed = (await OverdueAsync(T0.AddDays(60))).Select(o => o.OrderId).ToList();
        listed.Should().NotContain([delivered, cancelled, returned]);
    }

    [PostgresFact]
    public async Task A_later_status_change_never_moves_the_deadlines()
    {
        var (city, _) = await TwoDistrictsAsync();
        var id = (await ConfirmedAsync(city, city, T0)).OrderId;
        var before = await LoadAsync(id);

        await MoveAsync(id, (OrderStatus.Processing, null), (OrderStatus.Packed, null));
        var after = await LoadAsync(id);

        after.ConfirmedAtUtc.Should().Be(before.ConfirmedAtUtc);
        after.LegalDeliveryDeadlineUtc.Should().Be(before.LegalDeliveryDeadlineUtc);
        after.CourierHandoverDeadlineUtc.Should().Be(before.CourierHandoverDeadlineUtc);
    }

    [PostgresFact]
    public async Task A_configuration_change_affects_only_orders_confirmed_afterwards_and_reading_never_recomputes()
    {
        var (city, _) = await TwoDistrictsAsync();
        var first = (await ConfirmedAsync(city, city, T0)).OrderId;
        var second = (await ConfirmedAsync(city, city, T0, new DeliveryOptions { SameCityDays = 3, OtherCityDays = 7, HandoverHours = 24 }));

        (await LoadAsync(first)).LegalDeliveryDeadlineUtc.Should().Be(T0.AddDays(5), "stored at confirmation under the old numbers");
        var changed = await LoadAsync(second.OrderId);
        changed.LegalDeliveryDeadlineUtc.Should().Be(T0.AddDays(3));
        changed.CourierHandoverDeadlineUtc.Should().Be(T0.AddHours(24));

        // The buyer-facing read is the stored value, even from a service running with other numbers.
        await using var db = fixture.CreateContext();
        var inv = new InventoryService(db, new NoopLockProvider(), TimeProvider.System);
        var reader = new OrderService(db, inv, TimeProvider.System, ShippingTestKit.DevelopmentQuoter(db),
            new Mechware.Infrastructure.Pricing.PriceListResolver(db), null, new DeliveryOptions { SameCityDays = 30, OtherCityDays = 60, HandoverHours = 500 });
        var dto = await reader.GetAsync(second.Buyer, second.OrderId);
        dto.EstimatedDeliveryBy.Should().Be(T0.AddDays(3));
    }

    [PostgresFact]
    public async Task Paging_is_stable_ordered_by_deadline_and_capped_at_50()
    {
        var (city, _) = await TwoDistrictsAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++) ids.Add((await ConfirmedAsync(city, city, T0.AddMinutes(i - 100_000))).OrderId);
        var asOf = T0.AddDays(1);

        await using var db = fixture.CreateContext();
        var service = new OrderDeadlineService(db);
        var page1 = await service.ListOverdueAsync(asOf, 1, 2);
        var page2 = await service.ListOverdueAsync(asOf, 2, 2);
        page1.Items.Should().HaveCount(2);
        page1.TotalCount.Should().BeGreaterThanOrEqualTo(3);
        page1.Items.Concat(page2.Items).Select(o => o.OrderId).Should().OnlyHaveUniqueItems();
        page1.Items.Select(o => o.LegalDeliveryDeadlineUtc).Should().BeInAscendingOrder();
        ids.Should().OnlyContain(id => page1.Items.Concat(page2.Items).Any(o => o.OrderId == id));

        var capped = await service.ListOverdueAsync(asOf, 1, 5_000);
        capped.PageSize.Should().Be(50);
        capped.Items.Count.Should().BeLessThanOrEqualTo(50);
    }

    [PostgresFact]
    public async Task Confirming_an_order_without_Confirm_is_refused_by_the_aggregate()
    {
        var (city, _) = await TwoDistrictsAsync();
        var buyer = await TestUsers.CreateAsync(fixture);
        await using var seedDb = fixture.CreateContext();
        var v = await CatalogTestData.SeedVariantAsync(seedDb, onHand: 5, priceMinor: 100_000);
        var address = ShippingTestKit.AddressIn(buyer, city);
        seedDb.Addresses.Add(address);
        await seedDb.SaveChangesAsync();
        var inv = new InventoryService(seedDb, new NoopLockProvider(), TimeProvider.System);
        var orders = new OrderService(seedDb, inv, TimeProvider.System, ShippingTestKit.DevelopmentQuoter(seedDb), new Mechware.Infrastructure.Pricing.PriceListResolver(seedDb));
        var placed = await orders.PlaceAsync(new PlaceOrderRequest(buyer, Key(), [new PlaceOrderLine(v.VariantId, 1)], address.Id));

        (await LoadAsync(placed.OrderId)).LegalDeliveryDeadlineUtc.Should().BeNull("an unpaid order has no deadline yet");
        await using var db = fixture.CreateContext();
        var order = await db.Orders.Include(o => o.SubOrders).SingleAsync(o => o.Id == placed.OrderId);
        var act = () => order.Transition(OrderStatus.Confirmed, null, "sneaky", T0);
        act.Should().Throw<InvalidOperationException>();
    }
}
