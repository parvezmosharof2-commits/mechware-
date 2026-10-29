using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Domain.Catalog;
using ShopVerse.Domain.Inventory;
using ShopVerse.Domain.Reviews;
using ShopVerse.Infrastructure.Seed;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Catalog;

/// <summary>Seeds a throw-away database once and verifies the Phase 4 acceptance numbers.</summary>
public sealed class CatalogSeedFixture : IAsyncLifetime
{
    public PostgresDatabaseFixture Db { get; } = new();
    public SeedReport Report { get; private set; } = new();
    public string MediaRoot { get; } = Path.Combine(Path.GetTempPath(), "mw-seed-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        await Db.InitializeAsync();
        if (!TestInfra.PostgresAvailable) return;
        await using var ctx = Db.CreateContext();
        Report = await CatalogSeeder.SeedAsync(ctx, new SeedOptions { MediaRoot = MediaRoot });
    }

    public async Task DisposeAsync()
    {
        await Db.DisposeAsync();
        if (Directory.Exists(MediaRoot)) Directory.Delete(MediaRoot, recursive: true);
    }
}

public class CatalogSeedTests : IClassFixture<CatalogSeedFixture>
{
    private readonly CatalogSeedFixture _f;

    public CatalogSeedTests(CatalogSeedFixture fixture) => _f = fixture;

    [PostgresFact]
    public void Seed_FinishesWellUnderTwoMinutes()
    {
        _f.Report.Elapsed.Should().BeLessThan(TimeSpan.FromMinutes(2));
        Console.WriteLine($"seed took {_f.Report.Elapsed.TotalSeconds:F1}s");
    }

    [PostgresFact]
    public async Task Seed_ProducesRequiredVolumes()
    {
        await using var db = _f.Db.CreateContext();

        (await db.Categories.CountAsync()).Should().Be(12 + 36 + 72);
        (await db.Categories.CountAsync(c => c.Depth == 0)).Should().Be(12);
        (await db.Brands.CountAsync()).Should().Be(40);
        (await db.Sellers.CountAsync()).Should().Be(25);
        (await db.Products.CountAsync(p => p.Status == ProductStatus.Active)).Should().BeGreaterThanOrEqualTo(400);
        (await db.Reviews.CountAsync()).Should().BeGreaterThanOrEqualTo(3000);
        (await db.Questions.CountAsync()).Should().BeGreaterThan(100);
        (await db.ReviewMedia.CountAsync()).Should().BeGreaterThan(100);
    }

    [PostgresFact]
    public async Task EveryProduct_HasTwoToSixImages_Specs_AndBrandedBdtPricing()
    {
        await using var db = _f.Db.CreateContext();

        var imageCounts = await db.ProductMedia.GroupBy(m => m.ProductId).Select(g => g.Count()).ToListAsync();
        imageCounts.Should().OnlyContain(c => c >= 2 && c <= 6);
        (await db.Products.CountAsync()).Should().Be(imageCounts.Count);

        (await db.Products.CountAsync(p => p.SpecsJson == "{}" || p.HighlightsJson == "[]")).Should().Be(0);
        (await db.ProductVariants.CountAsync(v => v.PriceMinor <= 0)).Should().Be(0);
        (await db.ProductVariants.AnyAsync(v => v.Currency != ShopVerse.Domain.Common.Enums.Currency.BDT)).Should().BeFalse();
        (await db.ProductMedia.CountAsync(m => m.Width != 1200 || m.Height != 1200)).Should().Be(0);
    }

    [PostgresFact]
    public async Task Variants_ExistWhereNatural_AndSkusAreUnique()
    {
        await using var db = _f.Db.CreateContext();

        var multi = await db.ProductVariants.GroupBy(v => v.ProductId).CountAsync(g => g.Count() > 1);
        multi.Should().BeGreaterThan(80);
        var skus = await db.ProductVariants.Select(v => v.Sku).ToListAsync();
        skus.Should().OnlyHaveUniqueItems();
        (await db.ProductVariants.CountAsync(v => v.IsDefault)).Should().Be(await db.Products.CountAsync());
    }

    [PostgresFact]
    public async Task Stock_MixIncludesInStock_LowStock_AndOutOfStock_AndInvariantsHold()
    {
        await using var db = _f.Db.CreateContext();

        var perVariant = await db.InventoryItems.GroupBy(i => i.VariantId)
            .Select(g => g.Sum(i => i.OnHand - i.Reserved)).ToListAsync();
        perVariant.Count(a => a <= 0).Should().BeGreaterThan(10, "some variants are out of stock");
        perVariant.Count(a => a is > 0 and <= 5).Should().BeGreaterThan(20, "some variants are low on stock");
        perVariant.Count(a => a > 5).Should().BeGreaterThan(500);

        (await db.InventoryItems.CountAsync(i => i.OnHand < i.Reserved || i.Reserved < 0)).Should().Be(0);
    }

    [PostgresFact]
    public async Task Prices_ComparePrices_AndSales_AreCoherent()
    {
        await using var db = _f.Db.CreateContext();

        (await db.ProductVariants.CountAsync(v => v.CompareAtPriceMinor != null && v.CompareAtPriceMinor <= v.PriceMinor)).Should().Be(0);
        (await db.ProductVariants.CountAsync(v => v.CompareAtPriceMinor != null)).Should().BeGreaterThan(150);
        (await db.ProductVariants.CountAsync(v => v.SalePriceMinor != null && v.SalePriceMinor >= v.PriceMinor)).Should().Be(0);
        (await db.ProductVariants.CountAsync(v => v.SalePriceMinor != null)).Should().BeGreaterThan(20);
    }

    [PostgresFact]
    public async Task Reviews_AreWellFormed_AndAggregatesMatch()
    {
        await using var db = _f.Db.CreateContext();

        (await db.Reviews.CountAsync(r => r.Rating < 1 || r.Rating > 5)).Should().Be(0);
        (await db.Reviews.Select(r => r.Rating).Distinct().CountAsync()).Should().Be(5);

        var sample = await db.Products.Where(p => p.RatingCount > 0).OrderBy(p => p.Slug).Take(25).ToListAsync();
        foreach (var product in sample)
        {
            var published = await db.Reviews.Where(r => r.ProductId == product.Id && r.Status == ReviewStatus.Published).Select(r => r.Rating).ToListAsync();
            published.Count.Should().Be(product.RatingCount);
            product.RatingAvg.Should().Be(Math.Round((decimal)published.Average(), 2));
        }
    }

    [PostgresFact]
    public async Task Categories_FormTreeWithMaterializedPaths_AndLeavesHaveProducts()
    {
        await using var db = _f.Db.CreateContext();

        var all = await db.Categories.ToListAsync();
        foreach (var c in all.Where(c => c.ParentId != null))
        {
            var parent = all.Single(p => p.Id == c.ParentId);
            c.MaterializedPath.Should().StartWith(parent.MaterializedPath + ".");
            c.Depth.Should().Be(parent.Depth + 1);
        }

        var leafIds = all.Where(c => c.Depth == 2).Select(c => c.Id).ToList();
        var used = await db.Products.Select(p => p.CategoryId).Distinct().ToListAsync();
        leafIds.Should().OnlyContain(id => used.Contains(id));
        all.Where(c => c.Depth == 0).Should().OnlyContain(c => c.ImageUrl != null);
    }

    [PostgresFact]
    public async Task OutboxHoldsOneProductUpdatedEventPerProduct_WrittenAtomically()
    {
        await using var db = _f.Db.CreateContext();

        var productCount = await db.Products.CountAsync();
        (await db.OutboxMessages.CountAsync(o => o.EventType == nameof(ProductUpdatedEvent))).Should().Be(productCount);
        (await db.OutboxMessages.CountAsync(o => o.ProcessedAtUtc != null)).Should().Be(0);
    }

    [PostgresFact]
    public void Artwork_FilesWereWritten()
    {
        _f.Report.MediaFilesWritten.Should().BeGreaterThan(1500);
        File.Exists(Path.Combine(_f.MediaRoot, "brands", "siemens.svg")).Should().BeTrue();
        Directory.GetFiles(Path.Combine(_f.MediaRoot, "products"), "*.svg", SearchOption.AllDirectories).Length.Should().BeGreaterThan(1200);
    }

    [PostgresFact]
    public async Task Seeding_Twice_IsANoOp()
    {
        await using var db = _f.Db.CreateContext();
        var again = await CatalogSeeder.SeedAsync(db, new SeedOptions { MediaRoot = _f.MediaRoot, WriteMediaFiles = false });
        again.AlreadySeeded.Should().BeTrue();
        (await db.Products.CountAsync()).Should().Be(_f.Report.Products);
    }
}
