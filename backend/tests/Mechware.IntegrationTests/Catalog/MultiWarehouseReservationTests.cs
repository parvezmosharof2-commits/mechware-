using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Inventory;
using Mechware.Domain.Inventory;
using Mechware.Infrastructure.Catalog;
using Mechware.Infrastructure.Inventory;
using Mechware.IntegrationTests.Support;
using StackExchange.Redis;

namespace Mechware.IntegrationTests.Catalog;

/// <summary>
/// Finding #3 of the 2026-11-04 independent test: the storefront showed the stock of every warehouse added up (36), but
/// checkout reserved from one warehouse only, so ordering 19 failed with "18 left". A line is now split across active
/// warehouses when no single one has enough.
/// </summary>
public class MultiWarehouseReservationTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private async Task<T> With<T>(Func<InventoryService, Task<T>> action)
    {
        await using var db = fixture.CreateContext();
        return await action(new InventoryService(db, new NoopLockProvider(), TimeProvider.System));
    }

    private async Task<(Guid Variant, Guid FirstWarehouse, Guid SecondWarehouse)> TwoWarehousesAsync(int first, int second, bool secondActive = true)
    {
        await using var db = fixture.CreateContext();
        var v = await CatalogTestData.SeedVariantAsync(db, first);
        var w2 = await CatalogTestData.AddWarehouseStockAsync(db, v.VariantId, second, secondActive);
        return (v.VariantId, v.WarehouseId, w2);
    }

    private async Task<Dictionary<Guid, (int OnHand, int Reserved)>> BalancesAsync(Guid variant)
    {
        await using var db = fixture.CreateContext();
        return await db.InventoryItems.AsNoTracking().Where(i => i.VariantId == variant)
            .ToDictionaryAsync(i => i.WarehouseId, i => (i.OnHand, i.Reserved));
    }

    private static ReserveStockRequest Req(Guid reference, Guid variant, int qty) => new(reference, [new ReservationLine(variant, qty)]);

    [PostgresFact]
    public async Task ALineLargerThanAnyOneWarehouse_IsSplitAcrossWarehouses_AndCommitsAndCancelsCleanly()
    {
        var (variant, w1, w2) = await TwoWarehousesAsync(first: 18, second: 18);

        var all = Guid.NewGuid();
        (await With(s => s.ReserveAsync(Req(all, variant, 36)))).Succeeded.Should().BeTrue("the storefront shows 36");
        (await BalancesAsync(variant)).Should().BeEquivalentTo(new Dictionary<Guid, (int, int)> { [w1] = (18, 18), [w2] = (18, 18) });

        (await With(s => s.CommitAsync(all))).Should().Be(2, "one hold per warehouse");
        (await BalancesAsync(variant)).Should().BeEquivalentTo(new Dictionary<Guid, (int, int)> { [w1] = (0, 0), [w2] = (0, 0) });
    }

    [PostgresFact]
    public async Task AWarehouseThatCanSupplyTheWholeLine_IsPreferred_OneParcel()
    {
        var (variant, w1, w2) = await TwoWarehousesAsync(first: 5, second: 30);

        (await With(s => s.ReserveAsync(Req(Guid.NewGuid(), variant, 10)))).Succeeded.Should().BeTrue();
        (await BalancesAsync(variant)).Should().BeEquivalentTo(new Dictionary<Guid, (int, int)> { [w1] = (5, 0), [w2] = (30, 10) });
    }

    [PostgresFact]
    public async Task ASplitHold_IsReleasedFromEveryWarehouse_OnCancelAndOnExpiry()
    {
        var (variant, w1, w2) = await TwoWarehousesAsync(first: 10, second: 10);

        var cancelled = Guid.NewGuid();
        (await With(s => s.ReserveAsync(Req(cancelled, variant, 15)))).Succeeded.Should().BeTrue();
        (await With(s => s.CancelAsync(cancelled))).Should().Be(2);
        (await BalancesAsync(variant)).Values.Should().OnlyContain(b => b.Reserved == 0);

        var expiring = Guid.NewGuid();
        (await With(s => s.ReserveAsync(Req(expiring, variant, 15) with { Ttl = TimeSpan.FromMinutes(1) }))).Succeeded.Should().BeTrue();
        await With(s => s.ReleaseExpiredAsync(DateTime.UtcNow.AddMinutes(5)));
        (await BalancesAsync(variant)).Values.Should().OnlyContain(b => b.Reserved == 0);
        (await BalancesAsync(variant)).Keys.Should().BeEquivalentTo([w1, w2]);
    }

    [PostgresFact]
    public async Task MoreThanAllWarehousesTogether_IsRefused_WithTheTrueTotal_AndHoldsNothing()
    {
        var (variant, _, _) = await TwoWarehousesAsync(first: 18, second: 18);

        var result = await With(s => s.ReserveAsync(Req(Guid.NewGuid(), variant, 37)));

        result.Succeeded.Should().BeFalse();
        result.Shortfalls.Should().ContainSingle().Which.Available.Should().Be(36);
        (await BalancesAsync(variant)).Values.Should().OnlyContain(b => b.Reserved == 0);
    }

    [PostgresFact]
    public async Task AnInactiveWarehouse_IsNeverUsed()
    {
        var (variant, _, inactive) = await TwoWarehousesAsync(first: 10, second: 50, secondActive: false);

        var result = await With(s => s.ReserveAsync(Req(Guid.NewGuid(), variant, 11)));

        result.Succeeded.Should().BeFalse();
        result.Shortfalls.Single().Available.Should().Be(10);
        (await BalancesAsync(variant))[inactive].Reserved.Should().Be(0);
    }

    /// <summary>Splitting must not open an oversell: buyers racing for stock spread over warehouses never get more than exists.</summary>
    [PostgresFact]
    public async Task RacingSplitReservations_NeverOversell()
    {
        var (variant, _, _) = await TwoWarehousesAsync(first: 7, second: 8);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => With(s => s.ReserveAsync(Req(Guid.NewGuid(), variant, 2))))));

        results.Count(r => r.Succeeded).Should().Be(7, "15 units serve seven orders of 2");
        var balances = await BalancesAsync(variant);
        balances.Values.Sum(b => b.Reserved).Should().Be(14);
        balances.Values.Should().OnlyContain(b => b.Reserved <= b.OnHand);
    }

    /// <summary>The product list counted inactive warehouses too, so it could say "in stock" for stock checkout never sells.</summary>
    [PostgresAndRedisFact]
    public async Task TheProductList_CountsOnlyActiveWarehouses_LikeCheckout()
    {
        var (variant, _, _) = await TwoWarehousesAsync(first: 0, second: 50, secondActive: false);
        await using var db = fixture.CreateContext();
        var slug = await (from v in db.ProductVariants
                          join p in db.Products on v.ProductId equals p.Id
                          join c in db.Categories on p.CategoryId equals c.Id
                          where v.Id == variant
                          select c.Slug).SingleAsync();
        await using var redis = await ConnectionMultiplexer.ConnectAsync(TestInfra.RedisConnection);

        var page = await new StorefrontCatalogReader(db, redis, TimeProvider.System).ListProductsByCategoryAsync(slug, 1, 20);

        page.Value!.Items.Should().ContainSingle().Which.StockStatus.Should().Be("out_of_stock");
    }
}
