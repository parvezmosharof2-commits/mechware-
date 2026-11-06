using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Orders;
using ShopVerse.Domain.Catalog;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Infrastructure.Catalog;
using ShopVerse.Infrastructure.Inventory;
using ShopVerse.Infrastructure.Orders;
using ShopVerse.IntegrationTests.Support;
using StackExchange.Redis;

namespace ShopVerse.IntegrationTests.Catalog;

/// <summary>Sprint 3–4 (PIM): minimum order quantity and pack size are enforced on orders; lifecycle, successor and
/// alternatives are stored and shown on the product page.</summary>
public class ProductPimTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static string Key() => "key-" + Guid.NewGuid().ToString("N");

    private async Task<SeededVariant> SeedAsync(int onHand = 1_000, int minOrderQty = 1, int packSize = 1)
    {
        await using var db = fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(db, onHand);
        if (minOrderQty != 1 || packSize != 1)
        {
            var variant = await db.ProductVariants.SingleAsync(v => v.Id == seeded.VariantId);
            variant.SetOrderingRules(minOrderQty, packSize);
            await db.SaveChangesAsync();
        }

        return seeded;
    }

    private async Task<PlacedOrder> PlaceAsync(Guid buyer, Guid variantId, int quantity)
    {
        await using var db = fixture.CreateContext();
        var service = new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), TimeProvider.System);
        return await service.PlaceAsync(new PlaceOrderRequest(buyer, Key(), [new PlaceOrderLine(variantId, quantity)]));
    }

    private async Task<int> ReservedAsync(Guid variantId)
    {
        await using var db = fixture.CreateContext();
        return await db.InventoryItems.Where(i => i.VariantId == variantId).SumAsync(i => i.Reserved);
    }

    [PostgresFact]
    public async Task OrderingRules_AreStored()
    {
        var v = await SeedAsync(minOrderQty: 100, packSize: 50);
        await using var db = fixture.CreateContext();
        var stored = await db.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == v.VariantId);
        (stored.MinOrderQty, stored.PackSize).Should().Be((100, 50));
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
    public async Task TheDatabase_RefusesOrderingRulesThatBypassTheDomain()
    {
        var v = await SeedAsync();
        await using var db = fixture.CreateContext();

        var act = () => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE product_variants SET min_order_qty = 25, pack_size = 10 WHERE id = {v.VariantId}");

        await act.Should().ThrowAsync<Npgsql.PostgresException>();
    }

    [PostgresFact]
    public async Task LifecycleSuccessorAndAlternatives_AreStored()
    {
        var old = await SeedAsync();
        var successor = await SeedAsync();
        var alternative = await SeedAsync();
        await using (var db = fixture.CreateContext())
        {
            var p = await db.Products.Include(x => x.Alternatives).SingleAsync(x => x.Id == old.ProductId);
            p.SetLifecycle(ProductLifecycle.Obsolete, successor.ProductId, false, "Firmware V4: migrate the project.");
            p.AddAlternative(alternative.ProductId);
            await db.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext();
        var stored = await read.Products.AsNoTracking().Include(x => x.Alternatives).SingleAsync(x => x.Id == old.ProductId);
        (stored.Lifecycle, stored.SuccessorProductId, stored.SuccessorIsDropIn, stored.SuccessorNote)
            .Should().Be((ProductLifecycle.Obsolete, successor.ProductId, false, "Firmware V4: migrate the project."));
        stored.Alternatives.Select(a => a.AlternativeProductId).Should().Equal(alternative.ProductId);
    }

    [PostgresFact]
    public async Task AnObsoletePartWithStock_CanStillBeOrdered()
    {
        // Last-time buys: a maintenance team replacing a failed part in a running machine needs the exact part, not the successor.
        var buyer = await TestUsers.CreateAsync(fixture);
        var v = await SeedAsync(onHand: 3);
        await using (var db = fixture.CreateContext())
        {
            var p = await db.Products.SingleAsync(x => x.Id == v.ProductId);
            p.SetLifecycle(ProductLifecycle.Obsolete, null);
            await db.SaveChangesAsync();
        }

        (await PlaceAsync(buyer, v.VariantId, 2)).Status.Should().Be("pending_payment");
    }

    [PostgresAndRedisFact]
    public async Task TheProductPage_ShowsLifecycle_SuccessorWithCompatibility_ListedAlternatives_AndOrderingRules()
    {
        var old = await SeedAsync(minOrderQty: 10, packSize: 5);
        var successor = await SeedAsync();
        var alternative = await SeedAsync();
        var unlisted = await SeedAsync();
        string slug;
        await using (var db = fixture.CreateContext())
        {
            var p = await db.Products.Include(x => x.Alternatives).SingleAsync(x => x.Id == old.ProductId);
            p.SetLifecycle(ProductLifecycle.Nrnd, successor.ProductId, true, "Same footprint and wiring.");
            p.AddAlternative(unlisted.ProductId);
            p.AddAlternative(alternative.ProductId);
            var hidden = await db.Products.SingleAsync(x => x.Id == unlisted.ProductId);
            hidden.Archive();
            await db.SaveChangesAsync();
            slug = p.Slug;
        }

        using var redis = await ConnectionMultiplexer.ConnectAsync(TestInfra.RedisConnection);
        await using var readDb = fixture.CreateContext();
        var reader = new StorefrontCatalogReader(readDb, redis, TimeProvider.System);

        var page = (await reader.GetProductBySlugAsync(slug)).Value!;

        page.Lifecycle.Should().Be("nrnd");
        page.Successor!.IsDropIn.Should().BeTrue();
        page.Successor.Note.Should().Be("Same footprint and wiring.");
        await using var check = fixture.CreateContext();
        var successorSlug = await check.Products.Where(x => x.Id == successor.ProductId).Select(x => x.Slug).SingleAsync();
        var alternativeSlug = await check.Products.Where(x => x.Id == alternative.ProductId).Select(x => x.Slug).SingleAsync();
        page.Successor.Product.Slug.Should().Be(successorSlug);
        page.Alternatives!.Select(a => a.Slug).Should().Equal([alternativeSlug], "an archived alternative is not linked");
        page.Variants.Single().MinOrderQty.Should().Be(10);
        page.Variants.Single().PackSize.Should().Be(5);

        // The cached copy (second read comes from Redis) carries the same fields.
        var cached = (await reader.GetProductBySlugAsync(slug)).Value!;
        cached.Lifecycle.Should().Be("nrnd");
        cached.Successor!.Product.Slug.Should().Be(successorSlug);
        cached.Variants.Single().PackSize.Should().Be(5);
    }
}
