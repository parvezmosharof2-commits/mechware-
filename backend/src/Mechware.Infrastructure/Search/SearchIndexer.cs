using System.Runtime.CompilerServices;
using System.Text.Json;
using Mechware.Application.Search;
using Mechware.Infrastructure.Persistence.Outbox;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Mechware.Infrastructure.Search;

/// <summary>
/// Keeps the search index in step with PostgreSQL. Reads the outbox's Redis list (<see cref="SearchSyncQueuePublisher.QueueKey"/>)
/// with peek-then-trim, so a message leaves the queue only after the index has accepted the change: an engine outage delays
/// indexing, it never loses an update. Each message only names a product; its current state is always reloaded from the
/// database, so duplicates and out-of-order messages are harmless. Run exactly one indexer (the Worker host).
/// </summary>
public sealed class SearchIndexer(
    ProductSearchDocumentLoader loader, ISearchProvider search, IConnectionMultiplexer redis, ILogger<SearchIndexer> logger)
{
    public const int BatchSize = 200;
    public const int RebuildPageSize = 500;

    /// <summary>Set by <c>Mechware.Worker -- rebuild-search</c>; the running indexer picks it up on its next tick.</summary>
    public const string RebuildRequestKey = "search:rebuild:requested";

    internal string QueueKey { get; init; } = SearchSyncQueuePublisher.QueueKey;
    internal string RebuildKey { get; init; } = RebuildRequestKey;

    /// <summary>One tick: a requested rebuild first (queued messages wait and are applied after the swap), then drain the queue.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        if (await db.KeyExistsAsync(RebuildKey))
        {
            var count = await RebuildAsync(ct);
            await db.KeyDeleteAsync(RebuildKey);
            logger.LogInformation("Search index rebuilt with {Count} products", count);
        }

        var consumed = 0;
        int n;
        do { consumed += n = await ProcessBatchAsync(ct); } while (n == BatchSize);
        return consumed;
    }

    /// <summary>Applies up to <see cref="BatchSize"/> queued messages and returns how many left the queue.</summary>
    public async Task<int> ProcessBatchAsync(CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var messages = await db.ListRangeAsync(QueueKey, 0, BatchSize - 1);
        if (messages.Length == 0) return 0;

        var ids = new HashSet<Guid>();
        foreach (var message in messages)
        {
            if (TryReadProductId(message, out var id)) ids.Add(id);
            else logger.LogWarning("Dropping an unreadable search-sync message");
        }

        if (ids.Count > 0)
        {
            var documents = await loader.LoadAsync(ids, ct);
            await search.IndexProductsAsync(documents, ct);
            await search.DeleteProductsAsync(ids.Except(documents.Select(d => d.Id)).ToList(), ct);
        }

        // Only now: anything pushed meanwhile was appended after these, so trimming the head removes exactly what was applied.
        await db.ListTrimAsync(QueueKey, messages.Length, -1);
        return messages.Length;
    }

    /// <summary>Rebuilds the whole index from the database (fresh index + swap); returns the number of products indexed.</summary>
    public async Task<int> RebuildAsync(CancellationToken ct = default)
    {
        var count = 0;
        await search.ReplaceAllAsync(Pages(ct), ct);
        return count;

        async IAsyncEnumerable<IReadOnlyCollection<ProductSearchDocument>> Pages([EnumeratorCancellation] CancellationToken token = default)
        {
            Guid? after = null;
            while (true)
            {
                var page = await loader.LoadPageAsync(after, RebuildPageSize, token);
                if (page.Count == 0) yield break;
                count += page.Count;
                yield return page;
                if (page.Count < RebuildPageSize) yield break;
                after = page[^1].Id;
            }
        }
    }

    internal static bool TryReadProductId(RedisValue message, out Guid productId)
    {
        productId = Guid.Empty;
        try
        {
            using var json = JsonDocument.Parse((string)message!);
            var root = json.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("aggregateType", out var type) && type.ValueKind == JsonValueKind.String
                   && type.GetString() == "Product"
                   && root.TryGetProperty("aggregateId", out var id) && id.TryGetGuid(out productId)
                   && productId != Guid.Empty;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentNullException or InvalidOperationException)
        {
            return false;
        }
    }
}
