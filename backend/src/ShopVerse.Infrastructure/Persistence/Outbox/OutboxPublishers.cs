using System.Text.Json;
using ShopVerse.Application.Catalog;
using ShopVerse.Application.Common;
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

/// <summary>Evicts storefront cache entries when a product changes (tag-style invalidation).</summary>
public sealed class StorefrontCacheInvalidationPublisher : IOutboxPublisher
{
    private readonly IStorefrontCacheInvalidator _cache;

    public StorefrontCacheInvalidationPublisher(IStorefrontCacheInvalidator cache) => _cache = cache;

    public async Task PublishAsync(OutboxEnvelope message, CancellationToken ct = default)
    {
        switch (message.EventType)
        {
            case nameof(ProductUpdatedEvent):
                using (var doc = JsonDocument.Parse(message.Payload))
                {
                    var slug = doc.RootElement.GetProperty("slug").GetString();
                    if (slug is not null) await _cache.InvalidateProductAsync(slug, ct);
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
}
