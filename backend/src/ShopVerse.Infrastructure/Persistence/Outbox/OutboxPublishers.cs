using System.Text.Json;
using ShopVerse.Application.Catalog;
using ShopVerse.Application.Common;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Domain.Catalog;
using StackExchange.Redis;

namespace ShopVerse.Infrastructure.Persistence.Outbox;

/// <summary>
/// Pushes catalog events onto the Meilisearch sync queue (Redis list). Phase 5's indexer consumes
/// <c>search:sync:queue</c>; until then the queue simply buffers.
/// </summary>
public sealed class SearchSyncQueuePublisher : IOutboxPublisher
{
    public const string QueueKey = "search:sync:queue";

    private static readonly HashSet<string> Handled =
    [
        nameof(ProductUpdatedEvent), nameof(ProductSlugChangedEvent)
    ];

    private readonly IConnectionMultiplexer _redis;

    public SearchSyncQueuePublisher(IConnectionMultiplexer redis) => _redis = redis;

    public async Task PublishAsync(OutboxEnvelope message, CancellationToken ct = default)
    {
        if (!Handled.Contains(message.EventType)) return;
        var json = JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await _redis.GetDatabase().ListRightPushAsync(QueueKey, json);
    }
}

/// <summary>
/// Evicts storefront cache entries when a product changes (tag-style invalidation). A product page also shows other products
/// (its successor and its alternatives, only while they are on sale), so when a product changes, the pages that point to it
/// are evicted too; otherwise a page keeps linking to a part that has since gone obsolete or been archived.
/// </summary>
public sealed class StorefrontCacheInvalidationPublisher : IOutboxPublisher
{
    private readonly IStorefrontCacheInvalidator _cache;
    private readonly ShopVerseDbContext _db;

    public StorefrontCacheInvalidationPublisher(IStorefrontCacheInvalidator cache, ShopVerseDbContext db)
    {
        _cache = cache;
        _db = db;
    }

    public async Task PublishAsync(OutboxEnvelope message, CancellationToken ct = default)
    {
        switch (message.EventType)
        {
            case nameof(ProductUpdatedEvent):
                using (var doc = JsonDocument.Parse(message.Payload))
                {
                    var slug = doc.RootElement.GetProperty("slug").GetString();
                    if (slug is not null) await _cache.InvalidateProductAsync(slug, ct);
                    if (doc.RootElement.TryGetProperty("productId", out var id) && id.TryGetGuid(out var productId))
                        foreach (var dependent in await PagesPointingToAsync(productId, ct))
                            await _cache.InvalidateProductAsync(dependent, ct);
                }

                break;
            case nameof(ProductSlugChangedEvent):
                using (var doc = JsonDocument.Parse(message.Payload))
                {
                    foreach (var name in new[] { "oldSlug", "newSlug" })
                    {
                        var slug = doc.RootElement.GetProperty(name).GetString();
                        if (slug is not null) await _cache.InvalidateProductAsync(slug, ct);
                    }
                }

                break;
        }
    }

    /// <summary>Slugs of the products whose page shows <paramref name="productId"/> as successor or as an alternative.</summary>
    private Task<List<string>> PagesPointingToAsync(Guid productId, CancellationToken ct) =>
        _db.Products.AsNoTracking()
            .Where(p => p.SuccessorProductId == productId
                        || _db.Set<ProductAlternative>().Any(a => a.ProductId == p.Id && a.AlternativeProductId == productId))
            .Select(p => p.Slug).ToListAsync(ct);
}
