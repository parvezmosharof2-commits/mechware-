using System.Text.Json;
using FluentAssertions;
using Mechware.Application.Common;
using Mechware.Application.Inventory;
using Mechware.Application.Search;
using Mechware.Domain.Catalog;
using Mechware.Domain.Common.Exceptions;
using Mechware.Infrastructure.Catalog;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Search;
using Mechware.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Mechware.IntegrationTests.Search;

/// <summary>Needs PostgreSQL, Redis and Meilisearch; skips otherwise.</summary>
public sealed class SearchStackFactAttribute : FactAttribute
{
    public SearchStackFactAttribute()
    {
        if (!TestInfra.PostgresAvailable) Skip = "PostgreSQL not reachable (set MECHWARE_TEST_PG).";
        else if (!TestInfra.RedisAvailable) Skip = "Redis not reachable (set MECHWARE_TEST_REDIS).";
        else if (!MeiliInfra.Available) Skip = "Meilisearch not reachable (set MECHWARE_TEST_MEILI).";
    }
}

public sealed class SearchIndexerTests : IClassFixture<PostgresDatabaseFixture>, IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _pg;
    private readonly string _index = "mw_idx_" + Guid.NewGuid().ToString("N")[..12];
    private readonly string _queue = "test:search:queue:" + Guid.NewGuid().ToString("N");
    private readonly string _rebuild = "test:search:rebuild:" + Guid.NewGuid().ToString("N");
    private readonly string _dead = "test:search:dead:" + Guid.NewGuid().ToString("N");
    private readonly string _failures = "test:search:failures:" + Guid.NewGuid().ToString("N");
    private ConnectionMultiplexer _redis = null!;
    private MeilisearchSearchProvider _search = null!;

    public SearchIndexerTests(PostgresDatabaseFixture pg) => _pg = pg;

    private static bool StackUp => TestInfra.PostgresAvailable && TestInfra.RedisAvailable && MeiliInfra.Available;

    public async Task InitializeAsync()
    {
        if (!StackUp) return;
        _redis = await ConnectionMultiplexer.ConnectAsync(TestInfra.RedisConnection);
        _search = Provider(MeiliInfra.Url);
        await _search.EnsureIndexAsync();
    }

    public async Task DisposeAsync()
    {
        if (!StackUp) return;
        await _redis.GetDatabase().KeyDeleteAsync([_queue, _rebuild, _dead, _failures]);
        _redis.Dispose();
        using var http = new HttpClient { BaseAddress = new Uri(MeiliInfra.Url + "/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", MeiliInfra.Key);
        await http.DeleteAsync($"indexes/{_index}");
    }

    private MeilisearchSearchProvider Provider(string url) =>
        new(new HttpClient(), new SearchOptions { Url = url, ApiKey = MeiliInfra.Key, IndexName = _index, TaskTimeoutSeconds = 30 },
            NullLogger<MeilisearchSearchProvider>.Instance);

    private SearchIndexer Indexer(MechwareDbContext db, ISearchProvider? search = null, int pageSize = SearchIndexer.RebuildPageSize) =>
        new(new ProductSearchDocumentLoader(db, TimeProvider.System), search ?? _search, _redis, NullLogger<SearchIndexer>.Instance)
        {
            QueueKey = _queue,
            RebuildKey = _rebuild,
            DeadKey = _dead,
            RebuildFailuresCounterKey = _failures,
            PageSize = pageSize,
        };

    private RedisSearchReindexQueue ReindexQueue() =>
        new(_redis, TimeProvider.System, NullLogger<RedisSearchReindexQueue>.Instance) { QueueKey = _queue };

    /// <summary>Exactly what <c>SearchSyncQueuePublisher</c> pushes.</summary>
    private static string Envelope(Guid productId, string aggregateType = "Product") => JsonSerializer.Serialize(
        new OutboxEnvelope(Guid.NewGuid(), nameof(ProductUpdatedEvent), aggregateType, productId, "{}", DateTime.UtcNow),
        new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private Task Enqueue(params string[] messages) =>
        _redis.GetDatabase().ListRightPushAsync(_queue, messages.Select(m => (RedisValue)m).ToArray());

    private Task<long> QueueLength() => _redis.GetDatabase().ListLengthAsync(_queue);

    private async Task<ProductSearchDocument?> Indexed(Guid productId)
    {
        var all = await _search.SearchAsync(new SearchQuery { PageSize = 50 });
        return all.Hits.SingleOrDefault(h => h.Id == productId);
    }

    private async Task<SeededVariant> SeedAsync(int onHand = 5, string? partNumber = null)
    {
        await using var db = _pg.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(db, onHand);
        if (partNumber is not null)
        {
            var product = await db.Products.Include(p => p.Variants).SingleAsync(p => p.Id == seeded.ProductId);
            product.Variants.Single().SetManufacturerPartNumber(partNumber);
            await db.SaveChangesAsync();
        }
        return seeded;
    }

    [SearchStackFact]
    public async Task ActiveProduct_IsIndexedWithItsKeysPriceAndStock_AndLeavesTheQueue()
    {
        var seeded = await SeedAsync(onHand: 3, partNumber: "6ES7 214-1AG40-0XB0");
        await Enqueue(Envelope(seeded.ProductId));

        await using var db = _pg.CreateContext();
        (await Indexer(db).RunOnceAsync()).Should().Be(1);

        var doc = await Indexed(seeded.ProductId);
        doc.Should().NotBeNull();
        doc!.TitleEn.Should().Be("Test Product");
        doc.InStock.Should().BeTrue();
        doc.PriceMinor.Should().BeGreaterThan(0);
        doc.CategoryPath.Should().ContainSingle().Which.Should().StartWith("test-");
        doc.PartNumbers.Should().Contain("6ES7 214-1AG40-0XB0");
        doc.PartNumberKeys.Should().Contain("6ES72141AG400XB0");
        (await QueueLength()).Should().Be(0);

        (await _search.SearchAsync(new SearchQuery { PartNumberKey = "6ES72141AG400XB0" })).Hits.Should().ContainSingle();
    }

    [SearchStackFact]
    public async Task NumberAttributeInKw_ReachesTheIndexAsWattsInMeasures_AndNumericAttributes()
    {
        var seeded = await SeedAsync();
        var code = "pw_" + Guid.NewGuid().ToString("N")[..8];
        await using (var db = _pg.CreateContext())
        {
            var power = new AttributeDefinition(Guid.NewGuid(), "Power " + code, "Power " + code, code, AttributeDataType.Number, "kW", true);
            var value = power.AddValue(Guid.NewGuid(), "0.75 kW", "0.75 kW", 0);
            var unsafeCode = new AttributeDefinition(Guid.NewGuid(), "Odd " + code, "Odd " + code, "odd-" + code, AttributeDataType.Number, "kW", true);
            var unsafeValue = unsafeCode.AddValue(Guid.NewGuid(), "2 kW", "2 kW", 0);
            db.Attributes.AddRange(power, unsafeCode);
            var product = await db.Products.Include(p => p.AttributeValues).SingleAsync(p => p.Id == seeded.ProductId);
            product.AssignAttributeValue(value.Id);
            product.AssignAttributeValue(unsafeValue.Id);
            await db.SaveChangesAsync();
        }
        await Enqueue(Envelope(seeded.ProductId));
        await using (var db = _pg.CreateContext()) await Indexer(db).RunOnceAsync();

        var doc = await Indexed(seeded.ProductId);
        doc!.Measures["power"].Should().Equal(750d);
        doc.NumericAttributes.Should().ContainKey(code).WhoseValue.Should().Be(0.75);
        doc.NumericAttributes.Keys.Should().NotContain("odd-" + code, "codes outside the safe alphabet are not indexed");
        (await _search.SearchAsync(new SearchQuery { MeasureQuantity = "power", MeasureMin = 746, MeasureMax = 754 })).Hits
            .Should().ContainSingle(h => h.Id == seeded.ProductId);
        (await _search.SearchAsync(new SearchQuery { AttributeRanges = [new AttributeRange(code, 0.5, 1)] })).Hits
            .Should().ContainSingle(h => h.Id == seeded.ProductId);
    }

    [SearchStackFact]
    public async Task PartTypedWithDigitOneForLetterL_IsFoundThroughTheIndexedFuzzyKey()
    {
        var seeded = await SeedAsync(partNumber: "E3Z-LS61");
        await Enqueue(Envelope(seeded.ProductId));
        await using var db = _pg.CreateContext();
        await Indexer(db).RunOnceAsync();

        var typed = PartNumbers.Fuzzy(PartNumbers.Normalise("E3Z-1S61"));
        typed.Should().NotBe(PartNumbers.Normalise("E3Z-LS61"));
        (await _search.SearchAsync(new SearchQuery { PartNumberKey = typed })).Hits.Should().ContainSingle()
            .Which.Id.Should().Be(seeded.ProductId);
    }

    [SearchStackFact]
    public async Task ProductWithNoStock_IsIndexedAsOutOfStock()
    {
        var seeded = await SeedAsync(onHand: 0);
        await Enqueue(Envelope(seeded.ProductId));
        await using var db = _pg.CreateContext();
        await Indexer(db).RunOnceAsync();
        (await Indexed(seeded.ProductId))!.InStock.Should().BeFalse();
    }

    [SearchStackFact]
    public async Task ArchivedProduct_LeavesTheIndex()
    {
        var seeded = await SeedAsync();
        await Enqueue(Envelope(seeded.ProductId));
        await using (var db = _pg.CreateContext()) await Indexer(db).RunOnceAsync();
        (await Indexed(seeded.ProductId)).Should().NotBeNull();

        await using (var db = _pg.CreateContext())
        {
            var product = await db.Products.SingleAsync(p => p.Id == seeded.ProductId);
            product.Archive();
            await db.SaveChangesAsync();
        }
        await Enqueue(Envelope(seeded.ProductId));
        await using (var db = _pg.CreateContext()) await Indexer(db).RunOnceAsync();

        (await Indexed(seeded.ProductId)).Should().BeNull();
    }

    [SearchStackFact]
    public async Task UnreadableAndForeignMessages_AreDropped_AndTheRestStillApplied()
    {
        var seeded = await SeedAsync();
        await Enqueue("not json", "[]", "{\"aggregateType\":\"Product\",\"aggregateId\":\"nope\"}",
            Envelope(Guid.NewGuid(), aggregateType: "Order"), Envelope(seeded.ProductId));

        await using var db = _pg.CreateContext();
        (await Indexer(db).RunOnceAsync()).Should().Be(5);

        (await Indexed(seeded.ProductId)).Should().NotBeNull();
        (await QueueLength()).Should().Be(0);
    }

    [SearchStackFact]
    public async Task DuplicateMessages_GiveOneDocument()
    {
        var seeded = await SeedAsync();
        await Enqueue(Envelope(seeded.ProductId), Envelope(seeded.ProductId), Envelope(seeded.ProductId));
        await using var db = _pg.CreateContext();
        await Indexer(db).RunOnceAsync();

        var all = await _search.SearchAsync(new SearchQuery { PageSize = 50 });
        all.Hits.Count(h => h.Id == seeded.ProductId).Should().Be(1);
    }

    [SearchStackFact]
    public async Task EngineDown_KeepsTheMessages_SoNothingIsLost()
    {
        var seeded = await SeedAsync();
        await Enqueue(Envelope(seeded.ProductId));

        await using (var db = _pg.CreateContext())
        {
            var act = () => Indexer(db, Provider("http://127.0.0.1:1")).RunOnceAsync();
            await act.Should().ThrowAsync<ServiceUnavailableException>();
        }
        (await QueueLength()).Should().Be(1);

        await using (var db = _pg.CreateContext()) await Indexer(db).RunOnceAsync();
        (await Indexed(seeded.ProductId)).Should().NotBeNull();
        (await QueueLength()).Should().Be(0);
    }

    [SearchStackFact]
    public async Task MessagesPushedDuringABatch_AreNotTrimmedAway()
    {
        var first = await SeedAsync();
        var second = await SeedAsync();
        await Enqueue(Envelope(first.ProductId));

        await using var db = _pg.CreateContext();
        // The second message arrives while the first batch is being indexed (after it was read, before the trim).
        var arrivesMidBatch = new CallbackSearch(_search, () => Enqueue(Envelope(second.ProductId)));
        (await Indexer(db, arrivesMidBatch).ProcessBatchAsync()).Should().Be(1);
        (await QueueLength()).Should().Be(1, "the message that arrived mid-batch must survive the trim");

        await Indexer(db).RunOnceAsync();
        (await Indexed(first.ProductId)).Should().NotBeNull();
        (await Indexed(second.ProductId)).Should().NotBeNull();
        (await QueueLength()).Should().Be(0);
    }

    [SearchStackFact]
    public async Task RequestedRebuild_DropsStaleDocuments_KeepsSynonyms_AndClearsTheRequest()
    {
        var live = await SeedAsync();
        var stale = new ProductSearchDocument { Id = Guid.NewGuid(), Slug = "ghost", TitleEn = "Ghost product" };
        await _search.IndexProductsAsync([stale]);
        await _search.UpdateSynonymsAsync(new Dictionary<string, IReadOnlyList<string>> { ["gadget"] = ["product"] });
        await _redis.GetDatabase().StringSetAsync(_rebuild, "now");

        await using var db = _pg.CreateContext();
        await Indexer(db).RunOnceAsync();

        (await Indexed(stale.Id)).Should().BeNull("a full rebuild only contains what the database says is on sale");
        (await Indexed(live.ProductId)).Should().NotBeNull();
        (await _search.SearchAsync(new SearchQuery { Text = "gadget" })).Hits.Should().Contain(h => h.Id == live.ProductId);
        (await _redis.GetDatabase().KeyExistsAsync(_rebuild)).Should().BeFalse();
    }

    [SearchStackFact]
    public async Task SellingTheLastUnit_ReachesTheIndexAsOutOfStock()
    {
        var seeded = await SeedAsync(onHand: 1);
        await Enqueue(Envelope(seeded.ProductId));
        await using (var db = _pg.CreateContext()) await Indexer(db).RunOnceAsync();
        (await Indexed(seeded.ProductId))!.InStock.Should().BeTrue();

        await using (var db = _pg.CreateContext())
        {
            var inventory = new InventoryService(db, new NoopLockProvider(), TimeProvider.System, null, ReindexQueue());
            (await inventory.ReserveAsync(new ReserveStockRequest(Guid.NewGuid(), [new ReservationLine(seeded.VariantId, 1)]))).Succeeded.Should().BeTrue();
        }
        (await QueueLength()).Should().Be(1);

        await using (var db = _pg.CreateContext()) await Indexer(db).RunOnceAsync();
        (await Indexed(seeded.ProductId))!.InStock.Should().BeFalse();
    }

    [SearchStackFact]
    public async Task CategorySlugRename_MakesTheNewSlugFindTheProduct()
    {
        var seeded = await SeedAsync();
        Guid categoryId;
        string oldSlug;
        await using (var db = _pg.CreateContext())
        {
            var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == seeded.ProductId);
            var category = await db.Categories.AsNoTracking().SingleAsync(c => c.Id == product.CategoryId);
            categoryId = category.Id;
            oldSlug = category.Slug;
        }
        await Enqueue(Envelope(seeded.ProductId));
        await using (var db = _pg.CreateContext()) await Indexer(db).RunOnceAsync();
        (await _search.SearchAsync(new SearchQuery { CategorySlug = oldSlug })).Hits.Should().ContainSingle(h => h.Id == seeded.ProductId);

        var newSlug = "renamed-" + Guid.NewGuid().ToString("N")[..8];
        await using (var db = _pg.CreateContext())
            await new CatalogSlugService(db, ReindexQueue()).ChangeCategorySlugAsync(categoryId, newSlug);
        await using (var db = _pg.CreateContext()) await Indexer(db).RunOnceAsync();

        (await _search.SearchAsync(new SearchQuery { CategorySlug = newSlug })).Hits.Should().ContainSingle(h => h.Id == seeded.ProductId);
        (await _search.SearchAsync(new SearchQuery { CategorySlug = oldSlug })).Hits.Should().NotContain(h => h.Id == seeded.ProductId);
    }

    [SearchStackFact]
    public async Task PoisonProduct_GoesToTheDeadList_AndTheOthersAreIndexed()
    {
        var good = await SeedAsync();
        var poison = await SeedAsync();
        await Enqueue(Envelope(good.ProductId), Envelope(poison.ProductId));

        await using var db = _pg.CreateContext();
        (await Indexer(db, new PoisonSearch(_search, poison.ProductId)).RunOnceAsync()).Should().Be(2);

        (await Indexed(good.ProductId)).Should().NotBeNull();
        (await Indexed(poison.ProductId)).Should().BeNull();
        (await QueueLength()).Should().Be(0);
        var dead = await _redis.GetDatabase().ListRangeAsync(_dead);
        dead.Should().ContainSingle().Which.ToString().Should().Contain(poison.ProductId.ToString()).And.Contain("poison");
    }

    [SearchStackFact]
    public async Task FailingRebuild_IsRetried_ThenTheRequestIsCleared_AndTheQueueStillDrains()
    {
        var seeded = await SeedAsync();
        var broken = new RebuildFails(_search);
        await _redis.GetDatabase().StringSetAsync(_rebuild, "now");

        for (var attempt = 1; attempt <= SearchIndexer.MaxRebuildFailures; attempt++)
        {
            await Enqueue(Envelope(seeded.ProductId));
            await using var db = _pg.CreateContext();
            await Indexer(db, broken).RunOnceAsync();
            (await QueueLength()).Should().Be(0, "the queue is drained even when the rebuild fails");
            (await _redis.GetDatabase().KeyExistsAsync(_rebuild)).Should().Be(attempt < SearchIndexer.MaxRebuildFailures);
        }
        broken.Calls.Should().Be(SearchIndexer.MaxRebuildFailures);
        (await _redis.GetDatabase().KeyExistsAsync(_failures)).Should().BeFalse();
    }

    [SearchStackFact]
    public async Task ProductWhoseVariantsAreAllInactive_IsNotIndexed()
    {
        var seeded = await SeedAsync();
        await using (var db = _pg.CreateContext())
        {
            var product = await db.Products.Include(p => p.Variants).SingleAsync(p => p.Id == seeded.ProductId);
            foreach (var v in product.Variants) v.Deactivate();
            await db.SaveChangesAsync();
        }
        await Enqueue(Envelope(seeded.ProductId));
        await using var db2 = _pg.CreateContext();
        await Indexer(db2).RunOnceAsync();
        (await Indexed(seeded.ProductId)).Should().BeNull();
    }

    [SearchStackFact]
    public async Task Rebuild_PagesByKeyset_SoEveryProductIsIndexedExactlyOnce()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++) ids.Add((await SeedAsync()).ProductId);
        await _redis.GetDatabase().StringSetAsync(_rebuild, "now");

        await using var db = _pg.CreateContext();
        await Indexer(db, pageSize: 2).RunOnceAsync();

        var seen = new List<Guid>();
        for (var page = 1; page <= 20; page++)
        {
            var result = await _search.SearchAsync(new SearchQuery { Page = page, PageSize = 50 });
            seen.AddRange(result.Hits.Select(h => h.Id));
            if (result.Hits.Count < 50) break;
        }
        foreach (var id in ids) seen.Count(x => x == id).Should().Be(1);
        seen.Should().OnlyHaveUniqueItems();
    }

    [SearchStackFact]
    public async Task OnlyOneIndexerHoldsTheLease()
    {
        var key = "test:search:lock:" + Guid.NewGuid().ToString("N");
        var a = new SearchIndexLease(_redis, "a") { Key = key };
        var b = new SearchIndexLease(_redis, "b") { Key = key };
        try
        {
            (await a.TryAcquireOrRenewAsync()).Should().BeTrue();
            (await b.TryAcquireOrRenewAsync()).Should().BeFalse();
            (await a.TryAcquireOrRenewAsync()).Should().BeTrue("the holder renews");
            await a.ReleaseAsync();
            (await b.TryAcquireOrRenewAsync()).Should().BeTrue();
        }
        finally { await _redis.GetDatabase().KeyDeleteAsync(key); }
    }

    /// <summary>Fails indexing whenever the poison product is in the batch (a bad document).</summary>
    private sealed class PoisonSearch(ISearchProvider inner, Guid poisonId) : ISearchProvider
    {
        public Task IndexProductsAsync(IReadOnlyCollection<ProductSearchDocument> documents, CancellationToken ct = default) =>
            documents.Any(d => d.Id == poisonId) ? throw new InvalidOperationException("poison document") : inner.IndexProductsAsync(documents, ct);

        public Task EnsureIndexAsync(CancellationToken ct = default) => inner.EnsureIndexAsync(ct);
        public Task<SearchResultDto<ProductSearchDocument>> SearchAsync(SearchQuery query, CancellationToken ct = default) => inner.SearchAsync(query, ct);
        public Task<AutocompleteResultDto> AutocompleteAsync(string prefix, int limit = 5, CancellationToken ct = default) => inner.AutocompleteAsync(prefix, limit, ct);
        public Task DeleteProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default) => inner.DeleteProductsAsync(productIds, ct);
        public Task ReplaceAllAsync(IAsyncEnumerable<IReadOnlyCollection<ProductSearchDocument>> batches, CancellationToken ct = default) => inner.ReplaceAllAsync(batches, ct);
        public Task UpdateSynonymsAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> synonyms, CancellationToken ct = default) => inner.UpdateSynonymsAsync(synonyms, ct);
    }

    /// <summary>A rebuild that always fails.</summary>
    private sealed class RebuildFails(ISearchProvider inner) : ISearchProvider
    {
        public int Calls { get; private set; }

        public Task ReplaceAllAsync(IAsyncEnumerable<IReadOnlyCollection<ProductSearchDocument>> batches, CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException("rebuild failed");
        }

        public Task IndexProductsAsync(IReadOnlyCollection<ProductSearchDocument> documents, CancellationToken ct = default) => inner.IndexProductsAsync(documents, ct);
        public Task EnsureIndexAsync(CancellationToken ct = default) => inner.EnsureIndexAsync(ct);
        public Task<SearchResultDto<ProductSearchDocument>> SearchAsync(SearchQuery query, CancellationToken ct = default) => inner.SearchAsync(query, ct);
        public Task<AutocompleteResultDto> AutocompleteAsync(string prefix, int limit = 5, CancellationToken ct = default) => inner.AutocompleteAsync(prefix, limit, ct);
        public Task DeleteProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default) => inner.DeleteProductsAsync(productIds, ct);
        public Task UpdateSynonymsAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> synonyms, CancellationToken ct = default) => inner.UpdateSynonymsAsync(synonyms, ct);
    }

    /// <summary>Runs a callback once, just before the first indexing call, then delegates.</summary>
    private sealed class CallbackSearch(ISearchProvider inner, Func<Task> beforeFirstIndex) : ISearchProvider
    {
        private Func<Task>? _pending = beforeFirstIndex;

        public async Task IndexProductsAsync(IReadOnlyCollection<ProductSearchDocument> documents, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _pending, null) is { } callback) await callback();
            await inner.IndexProductsAsync(documents, ct);
        }

        public Task EnsureIndexAsync(CancellationToken ct = default) => inner.EnsureIndexAsync(ct);
        public Task<SearchResultDto<ProductSearchDocument>> SearchAsync(SearchQuery query, CancellationToken ct = default) => inner.SearchAsync(query, ct);
        public Task<AutocompleteResultDto> AutocompleteAsync(string prefix, int limit = 5, CancellationToken ct = default) => inner.AutocompleteAsync(prefix, limit, ct);
        public Task DeleteProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default) => inner.DeleteProductsAsync(productIds, ct);
        public Task ReplaceAllAsync(IAsyncEnumerable<IReadOnlyCollection<ProductSearchDocument>> batches, CancellationToken ct = default) => inner.ReplaceAllAsync(batches, ct);
        public Task UpdateSynonymsAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> synonyms, CancellationToken ct = default) => inner.UpdateSynonymsAsync(synonyms, ct);
    }

    [Fact]
    public void PublisherEnvelope_IsReadable()
    {
        var id = Guid.NewGuid();
        SearchIndexer.TryReadProductId(Envelope(id), out var read).Should().BeTrue();
        read.Should().Be(id);
        SearchIndexer.TryReadProductId(Envelope(id, "Order"), out _).Should().BeFalse();
        SearchIndexer.TryReadProductId("{}", out _).Should().BeFalse();
    }
}
