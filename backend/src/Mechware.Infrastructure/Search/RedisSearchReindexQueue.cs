using System.Text.Json;
using Mechware.Application.Search;
using Mechware.Infrastructure.Persistence.Outbox;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Mechware.Infrastructure.Search;

/// <summary>Pushes the same envelope the outbox publisher writes onto the search-sync list; failures are logged and swallowed.</summary>
public sealed class RedisSearchReindexQueue(IConnectionMultiplexer redis, TimeProvider clock, ILogger<RedisSearchReindexQueue> logger)
    : ISearchReindexQueue
{
    private const int PushBatch = 500;

    internal string QueueKey { get; init; } = SearchSyncQueuePublisher.QueueKey;

    public async Task EnqueueProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        if (productIds.Count == 0) return;
        try
        {
            var db = redis.GetDatabase();
            var now = clock.GetUtcNow().UtcDateTime;
            foreach (var chunk in productIds.Distinct().Chunk(PushBatch))
            {
                var values = chunk.Select(id => (RedisValue)JsonSerializer.Serialize(new
                {
                    id = Guid.NewGuid(),
                    eventType = "SearchReindexRequested",
                    aggregateType = "Product",
                    aggregateId = id,
                    payload = "{}",
                    occurredAtUtc = now
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web))).ToArray();
                await db.ListRightPushAsync(QueueKey, values);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not queue {Count} products for search reindexing; a rebuild will repair the index", productIds.Count);
        }
    }
}
