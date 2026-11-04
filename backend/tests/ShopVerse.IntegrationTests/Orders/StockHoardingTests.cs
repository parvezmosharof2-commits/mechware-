using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Orders;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Infrastructure.Inventory;
using ShopVerse.Infrastructure.Orders;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Orders;

/// <summary>
/// Finding #2 of the 2026-11-04 independent test: an account that never verified its e-mail could place order after
/// order and never pay, and each unpaid order held stock for 30 minutes, so one person could keep a product
/// "out of stock" for every other buyer.
/// </summary>
public class StockHoardingTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static string Key() => "key-" + Guid.NewGuid().ToString("N");

    private async Task<T> With<T>(Func<OrderService, Task<T>> action)
    {
        await using var db = fixture.CreateContext();
        return await action(new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), TimeProvider.System));
    }

    private static PlaceOrderRequest Req(Guid user, Guid variant, int qty = 1, string? key = null) =>
        new(user, key ?? Key(), [new PlaceOrderLine(variant, qty)]);

    private async Task<int> ReservedAsync(Guid variantId)
    {
        await using var db = fixture.CreateContext();
        return await db.InventoryItems.Where(i => i.VariantId == variantId).SumAsync(i => i.Reserved);
    }

    [PostgresFact]
    public async Task AnUnverifiedAccount_CannotPlaceAnOrder_AndHoldsNoStock()
    {
        var unverified = await TestUsers.CreateAsync(fixture, verified: false);
        SeededVariant v;
        await using (var db = fixture.CreateContext()) v = await CatalogTestData.SeedVariantAsync(db, 10);

        var act = () => With(s => s.PlaceAsync(Req(unverified, v.VariantId, 10)));

        (await act.Should().ThrowAsync<ForbiddenException>()).Which.Code.Should().Be("ACCOUNT_NOT_VERIFIED");
        (await ReservedAsync(v.VariantId)).Should().Be(0);
    }

    [PostgresFact]
    public async Task ABuyer_MayHaveOnlyAFewUnpaidOrders_AndCancellingOneFreesASlot()
    {
        var buyer = await TestUsers.CreateAsync(fixture);
        SeededVariant v;
        await using (var db = fixture.CreateContext()) v = await CatalogTestData.SeedVariantAsync(db, 50);

        var firstKey = Key();
        var placed = new List<PlacedOrder> { await With(s => s.PlaceAsync(Req(buyer, v.VariantId, key: firstKey))) };
        for (var i = 1; i < PlaceOrderRequest.MaxOpenUnpaidOrdersPerBuyer; i++)
            placed.Add(await With(s => s.PlaceAsync(Req(buyer, v.VariantId))));

        var oneTooMany = () => With(s => s.PlaceAsync(Req(buyer, v.VariantId)));
        (await oneTooMany.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("TOO_MANY_UNPAID_ORDERS");
        (await ReservedAsync(v.VariantId)).Should().Be(PlaceOrderRequest.MaxOpenUnpaidOrdersPerBuyer, "the refused order holds nothing");

        // Retrying an order that already exists is not a new order: the retry still gets its order back.
        var replay = await With(s => s.PlaceAsync(Req(buyer, v.VariantId, key: firstKey)));
        replay.Replayed.Should().BeTrue();
        replay.OrderId.Should().Be(placed[0].OrderId);

        await With(s => s.CancelAsync(buyer, placed[0].OrderId, "changed my mind"));
        (await With(s => s.PlaceAsync(Req(buyer, v.VariantId)))).Status.Should().Be("pending_payment");

        // Another buyer is not affected by this buyer's orders.
        var other = await TestUsers.CreateAsync(fixture);
        (await With(s => s.PlaceAsync(Req(other, v.VariantId)))).Status.Should().Be("pending_payment");
    }

    /// <summary>The cap is counted under a per-buyer lock: requests racing with different keys cannot all pass.</summary>
    [PostgresFact]
    public async Task ParallelOrdersWithDifferentKeys_NeverExceedTheCap()
    {
        var buyer = await TestUsers.CreateAsync(fixture);
        SeededVariant v;
        await using (var db = fixture.CreateContext()) v = await CatalogTestData.SeedVariantAsync(db, 50);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            try { await With(s => s.PlaceAsync(Req(buyer, v.VariantId))); return true; }
            catch (ConflictException e) when (e.Code == "TOO_MANY_UNPAID_ORDERS") { return false; }
        })));

        attempts.Count(ok => ok).Should().Be(PlaceOrderRequest.MaxOpenUnpaidOrdersPerBuyer);
        (await ReservedAsync(v.VariantId)).Should().Be(PlaceOrderRequest.MaxOpenUnpaidOrdersPerBuyer);
    }
}
