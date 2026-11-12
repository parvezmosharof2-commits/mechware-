using System.Runtime.CompilerServices;
using System.Text.Json;
using Mechware.Application.Search;
using Mechware.Domain.Common.Exceptions;
using Mechware.Infrastructure.Persistence.Outbox;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Mechware.Infrastructure.Search;

/// <summary>
/// Keeps the search index in step with PostgreSQL. Reads the outbox's Redis list (<see cref="SearchSyncQueuePublisher.QueueKey"/>)
/// with peek-then-trim, so a message leaves the queue only after the index has accepted the change: an engine outage delays
/// indexing, it never loses an update. Each message only names a product; its current state is always reloaded from the
/// database, so duplicates and out-of-order messages are harmless. Run one indexer at a time (<c>SearchIndexWorker</c> holds a
/// Redis lease for that).
/// <para>
/// A batch that throws is retried one product at a time. Rule: if every id fails with a <see cref="ServiceUnavailableException"/>
/// the engine is taken to be down, the original error is rethrown and the queue stays intact (an outage must not dead-letter
/// everything). Otherwise the ids that still fail (poison data, or an error that is not an engine outage) are pushed with a
/// reason to <see cref="DeadLetterKey"/> (capped at <see cref="DeadLetterCap"/>), logged as errors, and the batch is trimmed so
/// the queue keeps moving. A single-id batch that fails with an engine error is therefore always kept.
/// </para>
/// </summary>
public sealed class SearchIndexer(
    ProductSearchDocumentLoader loader, ISearchProvider search, IConnectionMultiplexer redis, ILogger<SearchIndexer> logger)
{
    public const int BatchSize = 200;
    public const int RebuildPageSize = 500;
    public const int DeadLetterCap = 1000;
    public const int MaxRebuildFailures = 3;
    private const int OutageProbeLimit = 3;
    public const string DeadLetterKey = "search:sync:dead";
    public const string RebuildFailuresKey = "search:rebuild:failures";

    /// <summary>Set by <c>Mechware.Worker -- rebuild-search</c>; the running indexer picks it up on its next tick.</summary>
    public const string RebuildRequestKey = "search:rebuild:requested";

    internal string QueueKey { get; init; } = SearchSyncQueuePublisher.QueueKey;
    internal string RebuildKey { get; init; } = RebuildRequestKey;
    internal string DeadKey { get; init; } = DeadLetterKey;
    internal string RebuildFailuresCounterKey { get; init; } = RebuildFailuresKey;
    internal int PageSize { get; init; } = RebuildPageSize;

    /// <summary>One tick: a requested rebuild first (queued messages wait and are applied after the swap), then drain the queue.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        if (await db.KeyExistsAsync(RebuildKey))
        {
            try
            {
                var count = await RebuildAsync(ct);
                await db.KeyDeleteAsync([RebuildKey, RebuildFailuresCounterKey]);
                logger.LogInformation("Search index rebuilt with {Count} products", count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Retry on the next tick, but not forever; the queue is drained either way.
                var failures = await db.StringIncrementAsync(RebuildFailuresCounterKey);
                if (failures >= MaxRebuildFailures)
                {
                    await db.KeyDeleteAsync([RebuildKey, RebuildFailuresCounterKey]);
                    logger.LogError(ex, "Search rebuild failed {Failures} times; the request was cleared", failures);
                }
                else logger.LogWarning(ex, "Search rebuild failed ({Failures}/{Max}); will retry", failures, MaxRebuildFailures);
            }
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
            try { await ApplyAsync(ids, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { await RetryOneByOneAsync(ids, ex, db, ct); }
        }

        // Only now: anything pushed meanwhile was appended after these, so trimming the head removes exactly what was applied.
        await db.ListTrimAsync(QueueKey, messages.Length, -1);
        return messages.Length;
    }

    private async Task ApplyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var documents = await loader.LoadAsync(ids, ct);
        await search.IndexProductsAsync(documents, ct);
        await search.DeleteProductsAsync(ids.Except(documents.Select(d => d.Id)).ToList(), ct);
    }

    private async Task RetryOneByOneAsync(HashSet<Guid> ids, Exception batchError, IDatabase db, CancellationToken ct)
    {
        var failed = new List<(Guid Id, Exception Error)>();
        var succeeded = 0;
        foreach (var id in ids)
        {
            try { await ApplyAsync([id], ct); succeeded++; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add((id, ex));
                // Three engine errors before any success: an outage, not bad data. Stop instead of waiting out a timeout per id.
                if (succeeded == 0 && failed.Count >= OutageProbeLimit && failed.All(f => f.Error is ServiceUnavailableException))
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(batchError).Throw();
            }
        }

        if (failed.Count == ids.Count && failed.All(f => f.Error is ServiceUnavailableException))
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(batchError).Throw(); // engine outage: keep the queue
        }

        foreach (var (id, error) in failed)
        {
            logger.LogError(error, "Search indexing of product {ProductId} failed; moved to the dead-letter list", id);
            await db.ListLeftPushAsync(DeadKey, JsonSerializer.Serialize(new
            {
                productId = id,
                reason = error.GetType().Name + ": " + error.Message,
                atUtc = DateTime.UtcNow
            }));
        }
        if (failed.Count > 0) await db.ListTrimAsync(DeadKey, 0, DeadLetterCap - 1);
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
                var (documents, scanned, lastId) = await loader.LoadPageAsync(after, PageSize, token);
                if (scanned == 0) yield break;
                count += documents.Count;
                if (documents.Count > 0) yield return documents;
                if (scanned < PageSize) yield break;
                after = lastId;
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
