using ShopVerse.Domain.Catalog;
using ShopVerse.Domain.Common.Enums;
using ShopVerse.Domain.Common.ValueObjects;
using ShopVerse.Domain.Identity;
using ShopVerse.Domain.Inventory;
using ShopVerse.Domain.Sellers;
using ShopVerse.Infrastructure.Persistence;

namespace ShopVerse.IntegrationTests.Support;

public sealed record SeededVariant(Guid ProductId, Guid VariantId, Guid WarehouseId, Guid InventoryItemId);

public static class CatalogTestData
{
    /// <summary>Inserts the minimal FK chain (user → seller → category → product → variant → warehouse → stock).</summary>
    public static async Task<SeededVariant> SeedVariantAsync(
        ShopVerseDbContext db, int onHand, int lowStockThreshold = 0, long priceMinor = 150_000)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = User.Create($"seller-{suffix}@example.test", null, null, "Test Seller");
        var seller = new Seller(Guid.NewGuid(), user.Id, $"Test Seller {suffix}", $"test-seller-{suffix}", null, null, null, 4.5m, 10, 99m, DateTime.UtcNow);
        var category = Category.CreateRoot(Guid.NewGuid(), "Test", "টেস্ট", $"test-{suffix}");
        var product = new Product(Guid.NewGuid(), seller.Id, category.Id, null, "Test Product", "টেস্ট পণ্য",
            $"test-product-{suffix}", "short", "সংক্ষিপ্ত", "<p>desc</p>");
        var variant = product.AddVariant(Guid.NewGuid(), $"SKU-{suffix}", null, new Dictionary<string, string> { ["Model"] = "A" },
            Money.FromMinorUnits(priceMinor, Currency.BDT), null, null, 0.5m, true);
        var warehouse = new Warehouse(Guid.NewGuid(), null, "Hub", $"HUB-{suffix}", "Dhaka", "Dhaka", "Gulshan");
        var item = new InventoryItem(Guid.NewGuid(), variant.Id, warehouse.Id, onHand, lowStockThreshold);

        db.Users.Add(user);
        db.Sellers.Add(seller);
        db.Categories.Add(category);
        db.Products.Add(product);
        db.Warehouses.Add(warehouse);
        await db.SaveChangesAsync();
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        return new SeededVariant(product.Id, variant.Id, warehouse.Id, item.Id);
    }
}
