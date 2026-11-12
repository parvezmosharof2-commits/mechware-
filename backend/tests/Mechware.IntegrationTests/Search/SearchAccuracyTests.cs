using FluentAssertions;
using Mechware.Application.Search;
using Mechware.Domain.Catalog;
using Mechware.Infrastructure.Search;
using Mechware.IntegrationTests.Catalog;
using Mechware.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace Mechware.IntegrationTests.Search;

/// <summary>Brief: exact part-number hits ≥ 98 % on the seeded catalogue, however buyers type the number.</summary>
public sealed class SearchAccuracyTests(CatalogSeedFixture seed, ITestOutputHelper output) : IClassFixture<CatalogSeedFixture>
{
    private const double Required = 0.98;

    [SearchStackFact]
    public async Task ExactPartNumber_IsFirstHit_InAtLeast98PercentOfTypedVariants()
    {
        var index = "mw_acc_" + Guid.NewGuid().ToString("N")[..12];
        var queue = "test:acc:queue:" + Guid.NewGuid().ToString("N");
        var rebuild = "test:acc:rebuild:" + Guid.NewGuid().ToString("N");
        var dead = "test:acc:dead:" + Guid.NewGuid().ToString("N");
        var failures = "test:acc:failures:" + Guid.NewGuid().ToString("N");
        using var redis = await ConnectionMultiplexer.ConnectAsync(TestInfra.RedisConnection);
        var provider = new MeilisearchSearchProvider(new HttpClient(),
            new SearchOptions { Url = MeiliInfra.Url, ApiKey = MeiliInfra.Key, IndexName = index, TaskTimeoutSeconds = 120 },
            NullLogger<MeilisearchSearchProvider>.Instance);
        try
        {
            await provider.EnsureIndexAsync();
            await using var db = seed.Db.CreateContext();
            var indexer = new SearchIndexer(new ProductSearchDocumentLoader(db, TimeProvider.System), provider, redis, NullLogger<SearchIndexer>.Instance)
            {
                QueueKey = queue,
                RebuildKey = rebuild,
                DeadKey = dead,
                RebuildFailuresCounterKey = failures,
            };
            await indexer.RebuildAsync();

            // Only products that are on sale are indexed (Active, not deleted), so only their variants can be found.
            var onSale = db.Products.AsNoTracking().Where(p => p.Status == ProductStatus.Active && p.DeletedAtUtc == null);
            var variants = await onSale.SelectMany(p => p.Variants)
                .Where(v => v.Status == VariantStatus.Active && v.ManufacturerPartNumber != null)
                .Select(v => new { v.ProductId, v.ManufacturerPartNumber })
                .ToListAsync();
            variants.Should().NotBeEmpty();

            // The seed also stores variant labels in the part-number field ("115", "25 × 25 mm slotted duct", "W22 IE3 15 kW 4P"). Those are
            // not part numbers a buyer would type as one: a real one is a single token that looks like a part number.
            var real = variants.Where(v => v.ManufacturerPartNumber!.All(c => !char.IsWhiteSpace(c))
                && PartNumbers.LooksLikePartNumber(v.ManufacturerPartNumber)).ToList();
            output.WriteLine($"variants with a part-number field={variants.Count}, real part numbers={real.Count}, variant labels excluded={variants.Count - real.Count}");
            real.Count.Should().BeGreaterThan(variants.Count / 2);

            var search = new StorefrontSearchService(provider);
            int total = 0, hits = 0, byPart = 0;
            var misses = new List<string>();
            foreach (var v in real)
            {
                var pn = v.ManufacturerPartNumber!;
                foreach (var (kind, typed) in Typings(pn))
                {
                    total++;
                    var result = await search.SearchAsync(new StorefrontSearchRequest { Text = typed, PageSize = 5 });
                    var ok = result.Hits.Count > 0 && result.Hits[0].Id == v.ProductId;
                    if (ok) hits++; else misses.Add($"{kind}: '{typed}' (from '{pn}') matchedBy={result.MatchedBy} first={(result.Hits.Count > 0 ? result.Hits[0].Slug : "none")}");
                    if (result.MatchedBy == StorefrontSearchService.MatchedByPartNumber) byPart++;
                }
            }

            var rate = (double)hits / total;
            output.WriteLine($"realVariants={real.Count} queries={total} firstHit={hits} ({rate:P2}) matchedByPartNumber={byPart} ({(double)byPart / total:P2})");
            foreach (var miss in misses.Take(60)) output.WriteLine("MISS " + miss);

            rate.Should().BeGreaterThanOrEqualTo(Required);
            ((double)byPart / total).Should().BeGreaterThanOrEqualTo(Required);
        }
        finally
        {
            await redis.GetDatabase().KeyDeleteAsync([queue, rebuild, dead, failures]);
            using var http = new HttpClient { BaseAddress = new Uri(MeiliInfra.Url + "/") };
            http.DefaultRequestHeaders.Authorization = new("Bearer", MeiliInfra.Key);
            await http.DeleteAsync($"indexes/{index}");
        }
    }

    /// <summary>Four ways a buyer types a number: lower case, no separators, spaces for separators, a look-alike letter for a digit.</summary>
    private static IEnumerable<(string Kind, string Typed)> Typings(string pn)
    {
        yield return ("lower", pn.ToLowerInvariant());
        yield return ("joined", new string(pn.Where(char.IsLetterOrDigit).ToArray()));
        yield return ("spaced", new string(pn.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray()));
        var at = pn.IndexOf('0');
        if (at >= 0) yield return ("zero-as-O", pn[..at] + "O" + pn[(at + 1)..]);
        else if ((at = pn.IndexOf('1')) >= 0) yield return ("one-as-l", pn[..at] + "l" + pn[(at + 1)..]);
    }
}
