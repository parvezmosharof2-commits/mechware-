using System.Text.Json;
using ShopVerse.Application.Catalog;
using StackExchange.Redis;

namespace ShopVerse.Infrastructure.Caching;

/// <summary>Key layout and TTLs for storefront read models (brief §6 principle 11).</summary>
public static class StorefrontCacheKeys
{
    public static readonly TimeSpan ProductTtl = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan StockTtl = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ListingTtl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(30);

    public static string Product(string slug) => $"sf:product:{slug}";
    public static string Redirect(string slug) => $"sf:redirect:{slug}";
    public static string Miss(string slug) => $"sf:miss:{slug}";
    public static string Stock(Guid productId) => $"sf:stock:{productId:N}";
    public static string Categories => "sf:categories";
    public static string Listing(string categorySlug, int page, int size) => $"sf:plp:{categorySlug}:{page}:{size}";
    public const string ListingPattern = "sf:plp:*";
}

/// <summary>Redis-backed invalidation used by the outbox relay and the inventory service.</summary>
public sealed class RedisStorefrontCacheInvalidator : IStorefrontCacheInvalidator
{
    private readonly IConnectionMultiplexer _redis;

    public RedisStorefrontCacheInvalidator(IConnectionMultiplexer redis) => _redis = redis;

    public async Task InvalidateProductAsync(string slug, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        await db.KeyDeleteAsync([StorefrontCacheKeys.Product(slug), StorefrontCacheKeys.Redirect(slug), StorefrontCacheKeys.Miss(slug)]);
        await DeleteListingsAsync();
    }

    public async Task InvalidateStockAsync(IEnumerable<Guid> productIds, CancellationToken ct = default)
    {
        var keys = productIds.Select(id => (RedisKey)StorefrontCacheKeys.Stock(id)).ToArray();
        if (keys.Length > 0) await _redis.GetDatabase().KeyDeleteAsync(keys);
    }

    public async Task InvalidateCategoriesAsync(CancellationToken ct = default)
    {
        await _redis.GetDatabase().KeyDeleteAsync(StorefrontCacheKeys.Categories);
        await DeleteListingsAsync();
    }

    private async Task DeleteListingsAsync()
    {
        foreach (var endpoint in _redis.GetEndPoints())
        {
            var server = _redis.GetServer(endpoint);
            if (server.IsReplica) continue;
            var keys = server.Keys(pattern: StorefrontCacheKeys.ListingPattern, pageSize: 250).ToArray();
            if (keys.Length > 0) await _redis.GetDatabase().KeyDeleteAsync(keys);
        }
    }
}

internal static class CacheJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
