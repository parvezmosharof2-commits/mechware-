using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Orders;
using Mechware.Domain.Catalog;
using Mechware.Domain.Common.Exceptions;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Orders;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Orders;

/// <summary>
/// PIM order units on the order itself: the cart rounds for the buyer, but <c>POST /orders</c> can be called directly, so the
/// order refuses a line below the SKU's minimum or not in whole packs, and never rounds it.
/// </summary>
public class OrderUnitsTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private async Task<SeededVariant> SeedAsync(int minOrderQty, int packSize, int onHand = 1_000)
    {
        await using var db = fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(db, onHand);
        var variant = await db.ProductVariants.SingleAsync(v => v.Id == seeded.VariantId);
        variant.SetOrderUnits(minOrderQty, packSize);
        await db.SaveChangesAsync();
        return seeded;
    }

    private async Task<PlacedOrder> PlaceAsync(Guid buyer, Guid variantId, int quantity)
    {
        await using var db = fixture.CreateContext();
        var service = new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), TimeProvider.System);
        return await service.PlaceAsync(new PlaceOrderRequest(buyer, "key-" + Guid.NewGuid().ToString("N"), [new PlaceOrderLine(variantId, quantity)]));
    }

    private async Task<int> ReservedAsync(Guid variantId)
    {
        await using var db = fixture.CreateContext();
        return await db.InventoryItems.Where(i => i.VariantId == variantId).SumAsync(i => i.Reserved);
    }

    [PostgresFact]
    public async Task AnOrderBelowTheMinimum_IsRefused_AndHoldsNoStock()
    {
        var buyer = await TestUsers.CreateAsync(fixture);
        var v = await SeedAsync(minOrderQty: 100, packSize: 50);

        var act = () => PlaceAsync(buyer, v.VariantId, 50);

        (await act.Should().ThrowAsync<ValidationException>()).Which.Errors.Single().Message.Should().Contain("minimum order is 100");
        (await ReservedAsync(v.VariantId)).Should().Be(0);
    }

    [PostgresFact]
    public async Task AnOrderThatIsNotWholePacks_IsRefused_NotRounded()
    {
        var buyer = await TestUsers.CreateAsync(fixture);
        var v = await SeedAsync(minOrderQty: 50, packSize: 50);

        var act = () => PlaceAsync(buyer, v.VariantId, 120);

        (await act.Should().ThrowAsync<ValidationException>()).Which.Errors.Single().Message.Should().Contain("packs of 50");
        (await ReservedAsync(v.VariantId)).Should().Be(0);
    }

    [PostgresFact]
    public async Task AnOrderOfWholePacksAboveTheMinimum_IsPlaced()
    {
        var buyer = await TestUsers.CreateAsync(fixture);
        var v = await SeedAsync(minOrderQty: 100, packSize: 50);

        var placed = await PlaceAsync(buyer, v.VariantId, 150);

        placed.Status.Should().Be("pending_payment");
        (await ReservedAsync(v.VariantId)).Should().Be(150);
    }

    [PostgresFact]
    public async Task AnObsoletePartWithStock_CanStillBeOrdered()
    {
        // Last-time buys: a maintenance team replacing a failed part in a running machine needs the exact part.
        var buyer = await TestUsers.CreateAsync(fixture);
        var v = await SeedAsync(minOrderQty: 1, packSize: 1, onHand: 3);
        await using (var db = fixture.CreateContext())
        {
            var p = await db.Products.SingleAsync(x => x.Id == v.ProductId);
            p.SetSupply(SupplyStatus.Obsolete, null);
            await db.SaveChangesAsync();
        }

        (await PlaceAsync(buyer, v.VariantId, 2)).Status.Should().Be("pending_payment");
    }
}
