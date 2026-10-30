using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Common;
using ShopVerse.Application.Inventory;
using ShopVerse.Domain.Inventory;
using ShopVerse.Infrastructure.Inventory;
using ShopVerse.Infrastructure.Persistence;
using ShopVerse.IntegrationTests.Support;
using StackExchange.Redis;

namespace ShopVerse.IntegrationTests.Catalog;

public class InventoryConcurrencyTests : IClassFixture<PostgresDatabaseFixture>
{
    private readonly PostgresDatabaseFixture _fixture;

    public InventoryConcurrencyTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    private InventoryService NewService(ShopVerseDbContext db, IDistributedLockProvider locks, TimeProvider? clock = null) =>
        new(db, locks, clock ?? TimeProvider.System);

    private async Task<(int ok, int insufficient, int busy)> RunParallelBuyersAsync(
        Guid variantId, int buyers, Func<ShopVerseDbContext, InventoryService> factory)
    {
        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, buyers).Select(_ => Task.Run(async () =>
        {
            await using var db = _fixture.CreateContext();
            var svc = factory(db);
            gate.Wait();
            return await svc.ReserveAsync(new ReserveStockRequest(Guid.NewGuid(), [new ReservationLine(variantId, 1)]));
        })).ToList();

        gate.Set();
        var results = await Task.WhenAll(tasks);
        return (results.Count(r => r.Succeeded), results.Count(r => !r.Succeeded && r.Shortfalls.Count > 0), results.Count(r => !r.Succeeded && r.Shortfalls.Count == 0));
    }

    [PostgresFact]
    public async Task FiftyParallelBuyers_TenUnits_ExactlyTenSucceed_PostgresRowLocksOnly()
    {
        await using var seedDb = _fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(seedDb, onHand: 10);

        var (ok, insufficient, busy) = await RunParallelBuyersAsync(seeded.VariantId, 50, db => NewService(db, new NoopLockProvider()));

        ok.Should().Be(10);
        insufficient.Should().Be(40);
        busy.Should().Be(0);

        await using var verify = _fixture.CreateContext();
        var item = await verify.InventoryItems.AsNoTracking().SingleAsync(i => i.Id == seeded.InventoryItemId);
        item.Reserved.Should().Be(10);
        item.Available.Should().Be(0);
        (await verify.StockReservations.CountAsync(r => r.VariantId == seeded.VariantId && r.Status == ReservationStatus.Active)).Should().Be(10);
    }

    [PostgresAndRedisFact]
    public async Task FiftyParallelBuyers_TenUnits_ExactlyTenSucceed_WithRedLock()
    {
        await using var seedDb = _fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(seedDb, onHand: 10);

        using var mux = await ConnectionMultiplexer.ConnectAsync(TestInfra.RedisConnection);
        using var locks = new RedLockProvider(mux);

        var (ok, insufficient, busy) = await RunParallelBuyersAsync(seeded.VariantId, 50, db => NewService(db, locks));

        ok.Should().Be(10);
        (insufficient + busy).Should().Be(40);

        await using var verify = _fixture.CreateContext();
        var item = await verify.InventoryItems.AsNoTracking().SingleAsync(i => i.Id == seeded.InventoryItemId);
        item.Reserved.Should().Be(10);
        item.OnHand.Should().BeGreaterThanOrEqualTo(item.Reserved);
    }

    [PostgresFact]
    public async Task MultiLineReservation_IsAllOrNothing()
    {
        await using var db = _fixture.CreateContext();
        var plenty = await CatalogTestData.SeedVariantAsync(db, onHand: 100);
        var scarce = await CatalogTestData.SeedVariantAsync(db, onHand: 1);

        var svc = NewService(db, new NoopLockProvider());
        var result = await svc.ReserveAsync(new ReserveStockRequest(Guid.NewGuid(),
        [
            new ReservationLine(plenty.VariantId, 5),
            new ReservationLine(scarce.VariantId, 2)
        ]));

        result.Succeeded.Should().BeFalse();
        result.Shortfalls.Should().ContainSingle(s => s.VariantId == scarce.VariantId && s.Available == 1);

        await using var verify = _fixture.CreateContext();
        (await verify.InventoryItems.AsNoTracking().SingleAsync(i => i.Id == plenty.InventoryItemId)).Reserved.Should().Be(0);
    }

    [PostgresFact]
    public async Task Reserve_IsIdempotentPerReference()
    {
        await using var db = _fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(db, onHand: 10);
        var reference = Guid.NewGuid();
        var request = new ReserveStockRequest(reference, [new ReservationLine(seeded.VariantId, 3)]);

        var first = await NewService(db, new NoopLockProvider()).ReserveAsync(request);
        await using var db2 = _fixture.CreateContext();
        var second = await NewService(db2, new NoopLockProvider()).ReserveAsync(request);

        first.Succeeded.Should().BeTrue();
        second.Succeeded.Should().BeTrue();
        second.ReservationIds.Should().BeEquivalentTo(first.ReservationIds);

        await using var verify = _fixture.CreateContext();
        (await verify.InventoryItems.AsNoTracking().SingleAsync(i => i.Id == seeded.InventoryItemId)).Reserved.Should().Be(3);
    }

    [PostgresFact]
    public async Task Commit_ConvertsHoldIntoDeduction_AndRaisesStockLowOnce()
    {
        await using var db = _fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(db, onHand: 8, lowStockThreshold: 5);
        var reference = Guid.NewGuid();

        var svc = NewService(db, new NoopLockProvider());
        (await svc.ReserveAsync(new ReserveStockRequest(reference, [new ReservationLine(seeded.VariantId, 4)]))).Succeeded.Should().BeTrue();
        (await svc.CommitAsync(reference)).Should().Be(1);

        await using var verify = _fixture.CreateContext();
        var item = await verify.InventoryItems.AsNoTracking().SingleAsync(i => i.Id == seeded.InventoryItemId);
        item.OnHand.Should().Be(4);
        item.Reserved.Should().Be(0);

        var lowEvents = await verify.OutboxMessages.AsNoTracking()
            .Where(o => o.EventType == nameof(StockLowDomainEvent) && o.AggregateId == seeded.InventoryItemId).ToListAsync();
        lowEvents.Should().HaveCount(1, "available crossed 5 once (8→4 on reserve); commit keeps it below and must not re-fire");
        lowEvents[0].Payload.Should().Contain(seeded.VariantId.ToString());
    }

    [PostgresFact]
    public async Task Cancel_ReleasesHold()
    {
        await using var db = _fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(db, onHand: 5);
        var reference = Guid.NewGuid();
        var svc = NewService(db, new NoopLockProvider());
        await svc.ReserveAsync(new ReserveStockRequest(reference, [new ReservationLine(seeded.VariantId, 5)]));
        (await svc.CancelAsync(reference)).Should().Be(1);

        await using var verify = _fixture.CreateContext();
        var item = await verify.InventoryItems.AsNoTracking().SingleAsync(i => i.Id == seeded.InventoryItemId);
        item.Reserved.Should().Be(0);
        item.OnHand.Should().Be(5);
    }

    [PostgresFact]
    public async Task DatabaseCheckConstraint_BlocksOversellEvenWithoutDomainGuard()
    {
        await using var db = _fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(db, onHand: 3);

        var act = async () => await db.Database.ExecuteSqlRawAsync(
            "UPDATE inventory_items SET reserved = 4 WHERE id = {0}", seeded.InventoryItemId);

        await act.Should().ThrowAsync<Exception>().WithMessage("*ck_inventory_available*");
    }
}
