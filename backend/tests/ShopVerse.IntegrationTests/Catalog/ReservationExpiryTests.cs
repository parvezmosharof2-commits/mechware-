using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Inventory;
using ShopVerse.Domain.Inventory;
using ShopVerse.Infrastructure.Inventory;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Catalog;

/// <summary>Own class (and therefore own database): the sweep is global, so it must not share rows with other tests.</summary>
public class ReservationExpiryTests : IClassFixture<PostgresDatabaseFixture>
{
    private readonly PostgresDatabaseFixture _fixture;

    public ReservationExpiryTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task ExpiredReservations_AreReleasedBySweep_AfterFifteenMinuteTtl()
    {
        await using var db = _fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(db, onHand: 10);
        var svc = new InventoryService(db, new NoopLockProvider(), TimeProvider.System);
        var reserveAt = DateTime.UtcNow;

        (await svc.ReserveAsync(new ReserveStockRequest(Guid.NewGuid(), [new ReservationLine(seeded.VariantId, 6, 100_000)]))).Succeeded.Should().BeTrue();

        // Within the TTL nothing is released.
        await using (var early = _fixture.CreateContext())
        {
            (await new InventoryService(early, new NoopLockProvider(), TimeProvider.System).ReleaseExpiredAsync(reserveAt.AddMinutes(14))).Should().Be(0);
        }

        await using (var late = _fixture.CreateContext())
        {
            (await new InventoryService(late, new NoopLockProvider(), TimeProvider.System).ReleaseExpiredAsync(reserveAt.AddMinutes(16))).Should().Be(1);
        }

        await using var verify = _fixture.CreateContext();
        var item = await verify.InventoryItems.AsNoTracking().SingleAsync(i => i.Id == seeded.InventoryItemId);
        item.Reserved.Should().Be(0);
        (await verify.StockReservations.AsNoTracking().SingleAsync(r => r.VariantId == seeded.VariantId)).Status.Should().Be(ReservationStatus.Expired);
    }

}
