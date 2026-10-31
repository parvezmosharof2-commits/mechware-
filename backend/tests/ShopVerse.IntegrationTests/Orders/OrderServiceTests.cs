using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Orders;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Orders;
using ShopVerse.Infrastructure.Inventory;
using ShopVerse.Infrastructure.Orders;
using ShopVerse.Infrastructure.Persistence;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Orders;

public class OrderServiceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static string Key() => "key-" + Guid.NewGuid().ToString("N");

    private async Task<Guid> NewBuyerAsync() => await TestUsers.CreateAsync(fixture);

    private async Task<T> With<T>(Func<OrderService, Task<T>> action, Func<DateTime, string>? orderNumbers = null)
    {
        await using var db = fixture.CreateContext();
        return await action(new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), TimeProvider.System, orderNumbers));
    }

    private async Task<SeededVariant> SeedAsync(int onHand, long price = 150_000)
    {
        await using var db = fixture.CreateContext();
        return await CatalogTestData.SeedVariantAsync(db, onHand, priceMinor: price);
    }

    private static PlaceOrderRequest Req(Guid user, string key, params (Guid Variant, int Qty)[] lines) =>
        new(user, key, lines.Select(l => new PlaceOrderLine(l.Variant, l.Qty)).ToList());

    private async Task<(int Reserved, int OnHand)> StockAsync(Guid variantId)
    {
        await using var db = fixture.CreateContext();
        var item = await db.InventoryItems.AsNoTracking().SingleAsync(i => i.VariantId == variantId);
        return (item.Reserved, item.OnHand);
    }

    [PostgresFact]
    public async Task PlacingAnOrder_PricesItFromTheDatabase_ReservesStock_AndRecordsEverything()
    {
        var buyer = await NewBuyerAsync();
        var v = await SeedAsync(onHand: 10, price: 150_000);

        var placed = await With(s => s.PlaceAsync(Req(buyer, Key(), (v.VariantId, 3))));

        // Guest checkout is VAT-inclusive: 3 x 1,500.00 stays 4,500.00 with the 5 % VAT inside it.
        placed.GrandTotalMinor.Should().Be(450_000);
        placed.Status.Should().Be("pending_payment");
        placed.Replayed.Should().BeFalse();
        OrderNumber.IsValid(placed.OrderNumber).Should().BeTrue();
        (await StockAsync(v.VariantId)).Reserved.Should().Be(3);

        await using var db = fixture.CreateContext();
        var order = await db.Orders.AsNoTracking().Include(o => o.SubOrders).ThenInclude(s => s.Items).Include(o => o.Events).SingleAsync(o => o.Id == placed.OrderId);
        order.UserId.Should().Be(buyer);
        order.SubOrders.Should().ContainSingle();
        var item = order.SubOrders.Single().Items.Single();
        (item.Quantity, item.UnitPriceMinor, item.TotalMinor, item.TaxMinor, item.NetMinor).Should().Be((3, 150_000, 450_000, 21_429, 428_571));
        order.GrandTotalMinor.Should().Be(order.SubOrders.Sum(s => s.TotalMinor));
        order.Events.Should().ContainSingle().Which.Reason.Should().Be("Order placed");
        order.SubOrders.Single().CommissionMinor.Should().Be(order.SubOrders.Single().GoodsNetMinor - order.SubOrders.Single().SellerEarningMinor);
    }

    [PostgresFact]
    public async Task ACartWithTwoSellers_BecomesOneOrderWithTwoSubOrders()
    {
        var buyer = await NewBuyerAsync();
        var a = await SeedAsync(5, 100_000);
        var b = await SeedAsync(5, 50_000);

        var placed = await With(s => s.PlaceAsync(Req(buyer, Key(), (a.VariantId, 1), (b.VariantId, 2))));

        await using var db = fixture.CreateContext();
        var order = await db.Orders.AsNoTracking().Include(o => o.SubOrders).SingleAsync(o => o.Id == placed.OrderId);
        order.SubOrders.Should().HaveCount(2);
        order.SubOrders.Select(s => s.SubOrderNumber).Should().BeEquivalentTo([$"{order.OrderNumber}-01", $"{order.OrderNumber}-02"]);   // a join has no row order
        order.GrandTotalMinor.Should().Be(200_000);
        order.SubOrders.Sum(s => s.TotalMinor).Should().Be(order.GrandTotalMinor);
    }

    [PostgresFact]
    public async Task TheSameKeyAndRequest_ReplaysTheFirstOrder_WithoutReservingAgain()
    {
        var buyer = await NewBuyerAsync();
        var v = await SeedAsync(10);
        var key = Key();

        var first = await With(s => s.PlaceAsync(Req(buyer, key, (v.VariantId, 2))));
        var second = await With(s => s.PlaceAsync(Req(buyer, key, (v.VariantId, 2))));

        second.OrderId.Should().Be(first.OrderId);
        second.OrderNumber.Should().Be(first.OrderNumber);
        second.Replayed.Should().BeTrue();
        (await StockAsync(v.VariantId)).Reserved.Should().Be(2, "a retry must not hold stock twice");
        await using var db = fixture.CreateContext();
        (await db.Orders.CountAsync(o => o.UserId == buyer)).Should().Be(1);
    }

    [PostgresFact]
    public async Task TheSameKeyWithADifferentRequest_IsRefused()
    {
        var buyer = await NewBuyerAsync();
        var v = await SeedAsync(10);
        var key = Key();
        await With(s => s.PlaceAsync(Req(buyer, key, (v.VariantId, 2))));

        var act = () => With(s => s.PlaceAsync(Req(buyer, key, (v.VariantId, 3))));
        (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("IDEMPOTENCY_KEY_REUSED");
        (await StockAsync(v.VariantId)).Reserved.Should().Be(2);
    }

    [PostgresFact]
    public async Task EightParallelRetriesOfOneKey_CreateExactlyOneOrder_AndHoldTheStockOnce()
    {
        var buyer = await NewBuyerAsync();
        var v = await SeedAsync(10);
        var key = Key();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => With(s => s.PlaceAsync(Req(buyer, key, (v.VariantId, 2)))))));

        // Every retry gets the one order back; none of them is told the stock ran out.
        results.Select(r => r.OrderId).Distinct().Should().ContainSingle();
        results.Count(r => !r.Replayed).Should().Be(1);
        await using var db = fixture.CreateContext();
        (await db.Orders.CountAsync(o => o.UserId == buyer)).Should().Be(1);
        (await StockAsync(v.VariantId)).Reserved.Should().Be(2);
    }

    [PostgresFact]
    public async Task TwentyBuyersForFiveUnits_ExactlyFiveOrders_AndNeverNegativeStock()
    {
        var v = await SeedAsync(onHand: 5);
        var buyers = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => NewBuyerAsync()));

        var outcomes = await Task.WhenAll(buyers.Select(b => Task.Run(async () =>
        {
            try { await With(s => s.PlaceAsync(Req(b, Key(), (v.VariantId, 1)))); return "ok"; }
            catch (ConflictException ex) { return ex.Code; }
        })));

        outcomes.Count(o => o == "ok").Should().Be(5);
        outcomes.Where(o => o != "ok").Should().OnlyContain(o => o == "INSUFFICIENT_STOCK");
        var (reserved, onHand) = await StockAsync(v.VariantId);
        (reserved, onHand).Should().Be((5, 5));
        await using var db = fixture.CreateContext();
        (await db.OrderItems.Where(i => i.VariantId == v.VariantId).CountAsync()).Should().Be(5);
    }

    [PostgresFact]
    public async Task APriceChange_AffectsNewOrders_ButNeverOneAlreadyPlaced()
    {
        var buyer = await NewBuyerAsync();
        var v = await SeedAsync(10, 100_000);
        var first = await With(s => s.PlaceAsync(Req(buyer, Key(), (v.VariantId, 1))));

        await using (var db = fixture.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE product_variants SET price_minor = 200000 WHERE id = {v.VariantId}");
        }

        var second = await With(s => s.PlaceAsync(Req(buyer, Key(), (v.VariantId, 1))));

        first.GrandTotalMinor.Should().Be(100_000);
        second.GrandTotalMinor.Should().Be(200_000, "the server prices from the database every time");
        await using var check = fixture.CreateContext();
        (await check.Orders.AsNoTracking().SingleAsync(o => o.Id == first.OrderId)).GrandTotalMinor.Should().Be(100_000);
    }

    [PostgresFact]
    public async Task UnknownOrWithdrawnItems_AreRefused_AndNothingIsReservedOrWritten()
    {
        var buyer = await NewBuyerAsync();
        var good = await SeedAsync(5);
        var withdrawn = await SeedAsync(5);
        await using (var db = fixture.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE product_variants SET status = 'inactive' WHERE id = {withdrawn.VariantId}");
        }

        foreach (var bad in new[] { Guid.NewGuid(), withdrawn.VariantId })
        {
            var act = () => With(s => s.PlaceAsync(Req(buyer, Key(), (good.VariantId, 1), (bad, 1))));
            (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("VARIANT_UNAVAILABLE");
        }

        (await StockAsync(good.VariantId)).Reserved.Should().Be(0);
        await using var check = fixture.CreateContext();
        (await check.Orders.CountAsync(o => o.UserId == buyer)).Should().Be(0);
    }

    [PostgresFact]
    public async Task AProductThatIsNotLive_CannotBeOrdered_EvenIfItsVariantIsActive()
    {
        var buyer = await NewBuyerAsync();
        var v = await SeedAsync(5);
        await using (var db = fixture.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE products SET status = 'draft' WHERE id = {v.ProductId}");
        }

        var act = () => With(s => s.PlaceAsync(Req(buyer, Key(), (v.VariantId, 1))));
        (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("VARIANT_UNAVAILABLE");
        (await StockAsync(v.VariantId)).Reserved.Should().Be(0);
    }

    [PostgresFact]
    public async Task AnOrderNumberClash_IsRetriedWithAFreshNumber_AndTheFirstAttemptsStockIsGivenBack()
    {
        var buyer = await NewBuyerAsync();
        var first = await SeedAsync(5);
        var placedFirst = await With(s => s.PlaceAsync(Req(buyer, Key(), (first.VariantId, 1))));

        var second = await SeedAsync(5);
        var attempts = 0;
        var placed = await With(s => s.PlaceAsync(Req(buyer, Key(), (second.VariantId, 2))),
            now => ++attempts == 1 ? placedFirst.OrderNumber : OrderNumber.Generate(now));   // first try collides with an existing number

        attempts.Should().Be(2);
        placed.OrderNumber.Should().NotBe(placedFirst.OrderNumber);
        (await StockAsync(second.VariantId)).Reserved.Should().Be(2, "the collided attempt released its hold; only the final order holds stock");
    }

    [PostgresFact]
    public async Task IfSavingFails_TheStockHeldForTheOrderIsReleased()
    {
        var buyer = await NewBuyerAsync();
        var v = await SeedAsync(5);

        await using (var db = fixture.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE OR REPLACE FUNCTION test_block_orders() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'orders are blocked for this test'; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER test_block_orders BEFORE INSERT ON orders FOR EACH ROW EXECUTE FUNCTION test_block_orders();
                """);
        }

        try
        {
            var act = () => With(s => s.PlaceAsync(Req(buyer, Key(), (v.VariantId, 3))));
            await act.Should().ThrowAsync<Exception>();
            (await StockAsync(v.VariantId)).Reserved.Should().Be(0, "an order that was not saved must not keep stock");
        }
        finally
        {
            await using var db = fixture.CreateContext();
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS test_block_orders ON orders; DROP FUNCTION IF EXISTS test_block_orders();");
        }
    }

    [PostgresFact]
    public async Task BadRequests_AreRefusedBeforeAnythingHappens()
    {
        var buyer = await NewBuyerAsync();
        var v = await SeedAsync(5);

        foreach (var (key, lines) in new (string, (Guid, int)[])[]
        {
            ("short", [(v.VariantId, 1)]),                       // key too short
            ("bad key with spaces!", [(v.VariantId, 1)]),
            (Key(), []),                                         // no lines
            (Key(), [(v.VariantId, 0)]),
            (Key(), [(v.VariantId, -2)]),
            (Key(), [(v.VariantId, 10_001)])
        })
        {
            var act = () => With(s => s.PlaceAsync(Req(buyer, key, lines)));
            await act.Should().ThrowAsync<ValidationException>();
        }

        (await StockAsync(v.VariantId)).Reserved.Should().Be(0);
    }

    [PostgresFact]
    public async Task ADuplicatedVariantIsOneLine_AndCountsTowardsTheStockLimitOnce()
    {
        var buyer = await NewBuyerAsync();
        var v = await SeedAsync(onHand: 4);

        var placed = await With(s => s.PlaceAsync(Req(buyer, Key(), (v.VariantId, 2), (v.VariantId, 2))));

        placed.GrandTotalMinor.Should().Be(4 * 150_000);
        (await StockAsync(v.VariantId)).Reserved.Should().Be(4);
        await using var db = fixture.CreateContext();
        (await db.OrderItems.CountAsync(i => i.VariantId == v.VariantId)).Should().Be(1);
    }

    [PostgresFact]
    public async Task TheDatabaseRefusesAnOrderWhoseMoneyDoesNotAddUp()
    {
        var buyer = await NewBuyerAsync();
        var v = await SeedAsync(5);
        var placed = await With(s => s.PlaceAsync(Req(buyer, Key(), (v.VariantId, 1))));

        await using var db = fixture.CreateContext();
        var act = () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE order_items SET total = total + 1 WHERE sub_order_id IN (SELECT id FROM sub_orders WHERE order_id = {placed.OrderId})");
        (await act.Should().ThrowAsync<Npgsql.PostgresException>()).Which.ConstraintName.Should().Be("ck_order_items_money");
    }
}
