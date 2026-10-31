using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopVerse.Application.Common;
using ShopVerse.Domain.Catalog;
using ShopVerse.Infrastructure.Caching;
using ShopVerse.Infrastructure.Catalog;
using ShopVerse.Infrastructure.Persistence.Outbox;
using ShopVerse.IntegrationTests.Support;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace ShopVerse.IntegrationTests.Catalog;

/// <summary>
/// Contains a wall-clock latency check (p95 under 50 ms across 3,200 requests). Such a test measures the machine as much as
/// the code, so this class never runs beside other test classes: they would compete for the same CPU.
/// </summary>
[Collection(ExclusiveTimingCollection.Name)]
public class StorefrontReadTests : IClassFixture<CatalogSeedFixture>, IAsyncLifetime
{
    private readonly CatalogSeedFixture _seed;
    private readonly ITestOutputHelper _output;
    private ConnectionMultiplexer? _redis;
    private WebApplicationFactory<Program>? _factory;

    public StorefrontReadTests(CatalogSeedFixture seed, ITestOutputHelper output)
    {
        _seed = seed;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        if (!TestInfra.PostgresAvailable || !TestInfra.RedisAvailable) return;
        _redis = await ConnectionMultiplexer.ConnectAsync(TestInfra.RedisConnection);
        var db = _redis.GetDatabase();
        foreach (var endpoint in _redis.GetEndPoints())
            foreach (var key in _redis.GetServer(endpoint).Keys(pattern: "sf:*")) await db.KeyDeleteAsync(key);
        await db.KeyDeleteAsync(SearchSyncQueuePublisher.QueueKey);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:DefaultConnection", _seed.Db.ConnectionString);
            b.UseSetting("ConnectionStrings:Redis", TestInfra.RedisConnection);
            b.UseSetting("RateLimiting:StorefrontPermitPerMinute", "10000000");   // the benchmark fires thousands of requests from one IP
        });
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        _redis?.Dispose();
        return Task.CompletedTask;
    }

    private async Task<string> AnySlugAsync(string prefix)
    {
        await using var db = _seed.Db.CreateContext();
        return await db.Products.AsNoTracking()
            .Where(p => p.Status == ProductStatus.Active && p.Slug.Contains(prefix))
            .OrderBy(p => p.Slug).Select(p => p.Slug).FirstAsync();
    }

    [PostgresAndRedisFact]
    public async Task GetProductBySlug_ReturnsRichProduct_FromDatabase_ThenRedis()
    {
        var slug = await AnySlugAsync("vfd007el21a");
        var client = _factory!.CreateClient();

        var first = await client.GetAsync($"/api/v1/storefront/products/{slug}");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Headers.CacheControl!.Public.Should().BeTrue();

        using var doc = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("slug").GetString().Should().Be(slug);
        data.GetProperty("titleEn").GetString().Should().Contain("VFD007EL21A");
        data.GetProperty("brand").GetProperty("name").GetString().Should().Be("Delta Electronics");
        data.GetProperty("breadcrumbs").GetArrayLength().Should().Be(3);
        data.GetProperty("media").GetArrayLength().Should().BeInRange(2, 6);
        data.GetProperty("specs").EnumerateObject().Should().NotBeEmpty();
        data.GetProperty("highlights").GetArrayLength().Should().BeGreaterThan(2);

        var variant = data.GetProperty("variants")[0];
        var price = variant.GetProperty("pricing").GetProperty("price");
        price.GetProperty("currency").GetString().Should().Be("BDT");
        price.GetProperty("amountMinor").GetInt64().Should().BeGreaterThan(0);
        price.GetProperty("formatted").GetString().Should().StartWith("৳");
        variant.GetProperty("pricing").GetProperty("taxIncluded").GetBoolean().Should().BeTrue();
        variant.GetProperty("stock").GetProperty("status").GetString().Should().BeOneOf("in_stock", "low_stock", "out_of_stock");

        (await _redis!.GetDatabase().KeyExistsAsync(StorefrontCacheKeys.Product(slug))).Should().BeTrue("the document is cached after the first read");
        var second = await client.GetAsync($"/api/v1/storefront/products/{slug}");
        second.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [PostgresAndRedisFact]
    public async Task UnknownSlug_Returns404Problem_AndIsNegativelyCached()
    {
        var client = _factory!.CreateClient();
        var response = await client.GetAsync("/api/v1/storefront/products/does-not-exist-123abc");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await _redis!.GetDatabase().KeyExistsAsync(StorefrontCacheKeys.Miss("does-not-exist-123abc"))).Should().BeTrue();
    }

    [PostgresAndRedisFact]
    public async Task ChangedSlug_Returns301ToNewSlug_AfterOutboxRelayEvictsCache()
    {
        var oldSlug = await AnySlugAsync("schneider-electric-altivar-12-atv12h018m2");
        var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        (await client.GetAsync($"/api/v1/storefront/products/{oldSlug}")).StatusCode.Should().Be(HttpStatusCode.OK);   // warms cache

        var newSlug = oldSlug + "-rev2";
        await using (var db = _seed.Db.CreateContext())
        {
            var product = await db.Products.SingleAsync(p => p.Slug == oldSlug);
            var history = product.ChangeSlug(newSlug, Guid.NewGuid())!;
            db.SlugHistories.Add(history);
            await db.SaveChangesAsync();   // outbox rows are written in this same transaction
        }

        // Cache still serves the old slug until the relay runs: eventual consistency by design.
        await using (var db = _seed.Db.CreateContext())
        {
            var invalidator = new RedisStorefrontCacheInvalidator(_redis!);
            IOutboxPublisher[] publishers = [new StorefrontCacheInvalidationPublisher(invalidator), new SearchSyncQueuePublisher(_redis!)];
            var relay = new OutboxRelay(db, publishers, NullLogger<OutboxRelay>.Instance);
            while (await relay.ProcessBatchAsync(500) > 0) { }
        }

        var moved = await client.GetAsync($"/api/v1/storefront/products/{oldSlug}");
        moved.StatusCode.Should().Be(HttpStatusCode.MovedPermanently);
        moved.Headers.Location!.ToString().Should().Be($"/api/v1/storefront/products/{newSlug}");

        (await client.GetAsync(moved.Headers.Location)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _redis!.GetDatabase().ListLengthAsync(SearchSyncQueuePublisher.QueueKey)).Should().BeGreaterThan(500, "every product event was queued for search sync");
    }

    [PostgresAndRedisFact]
    public async Task CategoryTree_AndListing_Work_IncludingDescendants()
    {
        var client = _factory!.CreateClient();

        using var tree = JsonDocument.Parse(await client.GetStringAsync("/api/v1/storefront/categories"));
        var roots = tree.RootElement.GetProperty("data");
        roots.GetArrayLength().Should().Be(12);
        roots[0].GetProperty("children").GetArrayLength().Should().Be(3);
        roots[0].GetProperty("children")[0].GetProperty("children").GetArrayLength().Should().Be(2);

        using var listing = JsonDocument.Parse(await client.GetStringAsync("/api/v1/storefront/categories/drives-motion-control/products?page=1&pageSize=12"));
        listing.RootElement.GetProperty("data").GetArrayLength().Should().Be(12);
        listing.RootElement.GetProperty("meta").GetProperty("totalCount").GetInt32().Should().BeGreaterThan(40);
        var first = listing.RootElement.GetProperty("data")[0];
        first.GetProperty("price").GetProperty("formatted").GetString().Should().StartWith("৳");
        first.GetProperty("imageUrl").GetString().Should().StartWith("/seed-media/");

        (await client.GetAsync("/api/v1/storefront/categories/nope/products")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [PostgresAndRedisFact]
    public async Task ProductBySlug_P95_IsUnder50ms_WhenCached()
    {
        var slug = await AnySlugAsync("acs580");
        var client = _factory!.CreateClient();
        var url = $"/api/v1/storefront/products/{slug}";

        (await client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK);   // prime Redis + JIT
        for (var i = 0; i < 200; i++) await client.GetAsync(url);                   // warm-up

        // Sequential latency
        var samples = new List<double>(2000);
        for (var i = 0; i < 2000; i++)
        {
            var sw = Stopwatch.StartNew();
            var r = await client.GetAsync(url);
            sw.Stop();
            r.StatusCode.Should().Be(HttpStatusCode.OK);
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        // 16-way concurrent latency
        var concurrent = new List<double>();
        await Parallel.ForEachAsync(Enumerable.Range(0, 3200), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (_, ct) =>
        {
            var sw = Stopwatch.StartNew();
            var r = await client.GetAsync(url, ct);
            sw.Stop();
            r.StatusCode.Should().Be(HttpStatusCode.OK);
            lock (concurrent) concurrent.Add(sw.Elapsed.TotalMilliseconds);
        });

        var seq = Percentiles(samples);
        var con = Percentiles(concurrent);
        _output.WriteLine($"sequential x2000  p50={seq.P50:F2}ms p95={seq.P95:F2}ms p99={seq.P99:F2}ms max={seq.Max:F2}ms");
        _output.WriteLine($"16-way     x3200  p50={con.P50:F2}ms p95={con.P95:F2}ms p99={con.P99:F2}ms max={con.Max:F2}ms");

        seq.P95.Should().BeLessThan(50);
        con.P95.Should().BeLessThan(50);
    }

    private static (double P50, double P95, double P99, double Max) Percentiles(List<double> values)
    {
        var s = values.OrderBy(v => v).ToList();
        double At(double p) => s[Math.Min(s.Count - 1, (int)Math.Ceiling(p * s.Count) - 1)];
        return (At(0.50), At(0.95), At(0.99), s[^1]);
    }
}
