using System.Text.Json;
using FluentAssertions;
using Mechware.Application.Common;
using Mechware.Application.Search;
using Mechware.Domain.Catalog;
using Mechware.Domain.Common.Exceptions;
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
        await _redis.GetDatabase().KeyDeleteAsync([_queue, _rebuild]);
        _redis.Dispose();
        using var http = new HttpClient { BaseAddress = new Uri(MeiliInfra.Url + "/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", MeiliInfra.Key);
        await http.DeleteAsync($"indexes/{_index}");
    }

    private MeilisearchSearchProvider Provider(string url) =>
        new(new HttpClient(), new SearchOptions { Url = url, ApiKey = MeiliInfra.Key, IndexName = _index, TaskTimeoutSeconds = 30 },
            NullLogger<MeilisearchSearchProvider>.Instance);

    private SearchIndexer Indexer(MechwareDbContext db, ISearchProvider? search = null) =>
        new(new ProductSearchDocumentLoader(db, TimeProvider.System), search ?? _search, _redis, NullLogger<SearchIndexer>.Instance)
        {
            QueueKey = _queue,
            RebuildKey = _rebuild,
        };

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
