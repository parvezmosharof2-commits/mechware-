using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Inventory;
using ShopVerse.Domain.Catalog;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Inventory;
using ShopVerse.Infrastructure.Catalog;
using ShopVerse.Infrastructure.Inventory;
using ShopVerse.Infrastructure.Persistence;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Catalog;

/// <summary>Regression tests for the audit findings that need a real database.</summary>
public class CatalogHardeningTests : IClassFixture<PostgresDatabaseFixture>
{
    private readonly PostgresDatabaseFixture _fixture;

    public CatalogHardeningTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    private static InventoryService Svc(ShopVerseDbContext db) => new(db, new NoopLockProvider(), TimeProvider.System);

    [PostgresFact]
    public async Task ReservedUnitPrice_ComesFromTheCatalogue_NotFromTheCaller()
    {
        await using var db = _fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(db, onHand: 5, priceMinor: 123_400);
        var reference = Guid.NewGuid();

        (await Svc(db).ReserveAsync(new ReserveStockRequest(reference, [new ReservationLine(seeded.VariantId, 1)]))).Succeeded.Should().BeTrue();

        await using var verify = _fixture.CreateContext();
        (await verify.StockReservations.AsNoTracking().SingleAsync(r => r.ReferenceId == reference)).LockedUnitPriceMinor.Should().Be(123_400);
    }

    [PostgresFact]
    public async Task DraftOrArchivedProducts_CannotBeReserved()
    {
        await using var db = _fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(db, onHand: 5);

        await using (var admin = _fixture.CreateContext())
        {
            var product = await admin.Products.SingleAsync(p => p.Id == seeded.ProductId);
            product.Archive();
            await admin.SaveChangesAsync();
        }

        var result = await Svc(db).ReserveAsync(new ReserveStockRequest(Guid.NewGuid(), [new ReservationLine(seeded.VariantId, 1)]));

        result.Succeeded.Should().BeFalse();
        result.UnavailableVariants.Should().Contain(seeded.VariantId);
        (await db.InventoryItems.AsNoTracking().SingleAsync(i => i.Id == seeded.InventoryItemId)).Reserved.Should().Be(0);
    }

    [PostgresFact]
    public async Task UnknownVariant_IsUnavailable()
    {
        await using var db = _fixture.CreateContext();
        var result = await Svc(db).ReserveAsync(new ReserveStockRequest(Guid.NewGuid(), [new ReservationLine(Guid.NewGuid(), 1)]));
        result.Succeeded.Should().BeFalse();
        result.UnavailableVariants.Should().HaveCount(1);
    }

    [PostgresFact]
    public async Task ReusingAReferenceWithDifferentLines_IsRejectedAsConflict()
    {
        await using var db = _fixture.CreateContext();
        var a = await CatalogTestData.SeedVariantAsync(db, onHand: 10);
        var b = await CatalogTestData.SeedVariantAsync(db, onHand: 10);
        var reference = Guid.NewGuid();
        await Svc(db).ReserveAsync(new ReserveStockRequest(reference, [new ReservationLine(a.VariantId, 2)]));

        await using var db2 = _fixture.CreateContext();
        var differentQty = async () => await Svc(db2).ReserveAsync(new ReserveStockRequest(reference, [new ReservationLine(a.VariantId, 3)]));
        await differentQty.Should().ThrowAsync<ConflictException>();

        await using var db3 = _fixture.CreateContext();
        var differentVariant = async () => await Svc(db3).ReserveAsync(new ReserveStockRequest(reference, [new ReservationLine(b.VariantId, 2)]));
        await differentVariant.Should().ThrowAsync<ConflictException>();
    }

    [PostgresFact]
    public async Task AbusiveRequests_AreBounded()
    {
        await using var db = _fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(db, onHand: 5);
        var line = new[] { new ReservationLine(seeded.VariantId, 1) };

        var longTtl = async () => await Svc(db).ReserveAsync(new ReserveStockRequest(Guid.NewGuid(), line, TimeSpan.FromDays(30)));
        await longTtl.Should().ThrowAsync<ValidationException>();

        var hugeQty = async () => await Svc(db).ReserveAsync(new ReserveStockRequest(Guid.NewGuid(), [new ReservationLine(seeded.VariantId, int.MaxValue)]));
        await hugeQty.Should().ThrowAsync<ValidationException>();

        var overflow = async () => await Svc(db).ReserveAsync(new ReserveStockRequest(Guid.NewGuid(),
            [new ReservationLine(seeded.VariantId, 10_000), new ReservationLine(seeded.VariantId, 10_000)]));
        await overflow.Should().ThrowAsync<ValidationException>();

        var manyLines = async () => await Svc(db).ReserveAsync(new ReserveStockRequest(Guid.NewGuid(),
            Enumerable.Range(0, 51).Select(_ => new ReservationLine(Guid.NewGuid(), 1)).ToList()));
        await manyLines.Should().ThrowAsync<ValidationException>();
    }

    [PostgresFact]
    public async Task SlugCanReturnToAnOldValue_AndKeepRedirecting()
    {
        await using var db = _fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(db, onHand: 1);
        var original = (await db.Products.AsNoTracking().SingleAsync(p => p.Id == seeded.ProductId)).Slug;

        foreach (var next in new[] { original + "-b", original, original + "-c" })   // A → B → A → C
        {
            await using var ctx = _fixture.CreateContext();
            await new CatalogSlugService(ctx).ChangeProductSlugAsync(seeded.ProductId, next);
        }

        await using var verify = _fixture.CreateContext();
        (await verify.Products.AsNoTracking().SingleAsync(p => p.Id == seeded.ProductId)).Slug.Should().Be(original + "-c");
        var retired = await verify.SlugHistories.AsNoTracking().Where(h => h.EntityId == seeded.ProductId).Select(h => h.OldSlug).ToListAsync();
        retired.Should().BeEquivalentTo(original, original + "-b");
    }

    [PostgresFact]
    public async Task SlugOwnedByAnotherProduct_IsRejected()
    {
        await using var db = _fixture.CreateContext();
        var a = await CatalogTestData.SeedVariantAsync(db, onHand: 1);
        var b = await CatalogTestData.SeedVariantAsync(db, onHand: 1);
        var bSlug = (await db.Products.AsNoTracking().SingleAsync(p => p.Id == b.ProductId)).Slug;

        await using var ctx = _fixture.CreateContext();
        var act = async () => await new CatalogSlugService(ctx).ChangeProductSlugAsync(a.ProductId, bSlug);
        await act.Should().ThrowAsync<ConflictException>();
    }

    [PostgresFact]
    public async Task CategorySlugChange_RewritesDescendantPaths_AndRedirects()
    {
        await using var db = _fixture.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var root = Category.CreateRoot(Guid.NewGuid(), "R", "আর", $"root-{suffix}");
        var sub = Category.CreateChild(root, Guid.NewGuid(), "S", "এস", $"sub-{suffix}");
        var leaf = Category.CreateChild(sub, Guid.NewGuid(), "L", "এল", $"leaf-{suffix}");
        db.Categories.AddRange(root, sub, leaf);
        await db.SaveChangesAsync();

        await using var ctx = _fixture.CreateContext();
        await new CatalogSlugService(ctx).ChangeCategorySlugAsync(root.Id, $"renamed-{suffix}");

        await using var verify = _fixture.CreateContext();
        (await verify.Categories.AsNoTracking().SingleAsync(c => c.Id == leaf.Id)).MaterializedPath.Should().Be($"root.renamed-{suffix}.sub-{suffix}.leaf-{suffix}");
        (await verify.SlugHistories.AsNoTracking().AnyAsync(h => h.OldSlug == $"root-{suffix}" && h.EntityId == root.Id)).Should().BeTrue();
    }

    [PostgresFact]
    public async Task ConcurrentProductEdits_SecondWriterGets409_NotALostUpdate()
    {
        await using var seedDb = _fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(seedDb, onHand: 1);

        await using var first = _fixture.CreateContext();
        await using var second = _fixture.CreateContext();
        var a = await first.Products.SingleAsync(p => p.Id == seeded.ProductId);
        var b = await second.Products.SingleAsync(p => p.Id == seeded.ProductId);

        a.SetContent("[\"A\"]", "{}", null, null);
        await first.SaveChangesAsync();

        b.SetContent("[\"B\"]", "{}", null, null);
        var act = async () => await second.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }
}

public class MigrationTests
{
    [PostgresFact]
    public async Task MigrationsApplyToAnEmptyDatabase_AndMatchTheModel()
    {
        var name = "mw_mig_" + Guid.NewGuid().ToString("N");
        await using (var admin = new Npgsql.NpgsqlConnection(TestInfra.AdminConnection))
        {
            await admin.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            var cs = new Npgsql.NpgsqlConnectionStringBuilder(TestInfra.AdminConnection) { Database = name }.ConnectionString;
            await using var db = new ShopVerseDbContext(new DbContextOptionsBuilder<ShopVerseDbContext>().UseNpgsql(cs).Options);

            await db.Database.MigrateAsync();   // throws PendingModelChangesWarning if the model drifted from the snapshot

            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        }
        finally
        {
            Npgsql.NpgsqlConnection.ClearAllPools();
            await using var admin = new Npgsql.NpgsqlConnection(TestInfra.AdminConnection);
            await admin.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
