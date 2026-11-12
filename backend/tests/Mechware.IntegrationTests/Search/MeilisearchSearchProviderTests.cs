using FluentAssertions;
using Mechware.Application.Search;
using Mechware.Domain.Catalog;
using Mechware.Domain.Common.Exceptions;
using Mechware.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mechware.IntegrationTests.Search;

/// <summary>Skips (like the PostgreSQL tests) when no Meilisearch answers; set <c>MECHWARE_TEST_MEILI</c> / <c>MECHWARE_TEST_MEILI_KEY</c> to point elsewhere.</summary>
public sealed class MeiliFactAttribute : FactAttribute
{
    public MeiliFactAttribute()
    {
        if (!MeiliInfra.Available) Skip = "Meilisearch not reachable (set MECHWARE_TEST_MEILI).";
    }
}

internal static class MeiliInfra
{
    public static readonly string Url = Environment.GetEnvironmentVariable("MECHWARE_TEST_MEILI") ?? "http://localhost:7700";
    public static readonly string Key = Environment.GetEnvironmentVariable("MECHWARE_TEST_MEILI_KEY") ?? "meili_master_key_12345";

    public static readonly bool Available = Probe();

    private static bool Probe()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            return http.GetAsync(Url.TrimEnd('/') + "/health").GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch { return false; }
    }
}

public sealed class MeilisearchSearchProviderTests : IAsyncLifetime
{
    private readonly string _index = "mw_test_" + Guid.NewGuid().ToString("N")[..12];
    private MeilisearchSearchProvider _search = null!;

    private MeilisearchSearchProvider Make(string? url = null, string? key = null, string? index = null) =>
        new(new HttpClient(), new SearchOptions { Url = url ?? MeiliInfra.Url, ApiKey = key ?? MeiliInfra.Key, IndexName = index ?? _index, TaskTimeoutSeconds = 30 },
            NullLogger<MeilisearchSearchProvider>.Instance);

    public async Task InitializeAsync()
    {
        if (!MeiliInfra.Available) return;
        _search = Make();
        await _search.EnsureIndexAsync();
    }

    public async Task DisposeAsync()
    {
        if (!MeiliInfra.Available) return;
        using var http = new HttpClient { BaseAddress = new Uri(MeiliInfra.Url + "/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", MeiliInfra.Key);
        await http.DeleteAsync($"indexes/{_index}");
    }

    private static ProductSearchDocument Doc(string title, string brand = "siemens", string[]? pns = null, long price = 10_000, bool inStock = true, int sold = 0,
        string category = "plc", string? titleBn = null) => new()
        {
            Id = Guid.NewGuid(),
            Slug = title.ToLowerInvariant().Replace(' ', '-'),
            TitleEn = title,
            TitleBn = titleBn ?? "",
            BrandName = brand.ToUpperInvariant(),
            BrandSlug = brand,
            CategoryPath = ["automation", category],
            PartNumbers = pns ?? [],
            PriceMinor = price,
            InStock = inStock,
            SoldCount = sold,
        };

    [MeiliFact]
    public async Task EnsureIndex_CanRunTwice_AndAppliesTheSettings()
    {
        await _search.EnsureIndexAsync();

        using var http = new HttpClient { BaseAddress = new Uri(MeiliInfra.Url + "/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", MeiliInfra.Key);
        var settings = await http.GetStringAsync($"indexes/{_index}/settings");
        settings.Should().Contain("partNumbers").And.Contain("brandSlug").And.Contain("soldCount:desc");
    }

    [MeiliFact]
    public async Task ExactPartNumberKey_FindsOnlyThatPart_AndAWrongDigitFindsNothing()
    {
        var plc = Doc("S7-1200 compact controller", pns: ["6ES7 214-1AG40-0XB0"]) with { PartNumberKeys = ["6ES72141AG400XB0"] };
        var near = Doc("S7-1200 other variant", pns: ["6ES7 214-1AG40-0XB1"]) with { PartNumberKeys = ["6ES72141AG400XB1"] };
        await _search.IndexProductsAsync([plc, near]);

        var hit = await _search.SearchAsync(new SearchQuery { PartNumberKey = "6ES72141AG400XB0" });
        hit.Hits.Should().ContainSingle().Which.Id.Should().Be(plc.Id);

        // One wrong character is another part, never a typo of this one.
        (await _search.SearchAsync(new SearchQuery { PartNumberKey = "6ES72141AG400XB9" })).Hits.Should().BeEmpty();
    }

    [MeiliFact]
    public async Task FuzzyKey_FindsAPartTypedWithOInsteadOfZero()
    {
        // The index holds the exact and the fuzzy key (O→0, I/L→1); the caller sends Fuzzy(Normalise(input)).
        const string raw = "E3Z-LS61";
        var key = PartNumbers.Normalise(raw);
        var sensor = Doc("Photoelectric sensor", brand: "omron", pns: [raw]) with { PartNumberKeys = [key, PartNumbers.Fuzzy(key)] };
        await _search.IndexProductsAsync([sensor, Doc("Other sensor", brand: "omron", pns: ["E3Z-D61"]) with { PartNumberKeys = ["E3ZD61"] }]);

        var typed = PartNumbers.Fuzzy(PartNumbers.Normalise("E3Z-1S61")); // buyer typed digit one instead of letter L
        typed.Should().NotBe(key);
        (await _search.SearchAsync(new SearchQuery { PartNumberKey = typed })).Hits.Should().ContainSingle().Which.Id.Should().Be(sensor.Id);
    }

    [MeiliFact]
    public async Task RawPartNumberText_IsFoundByFreeTextSearch()
    {
        var plc = Doc("S7-1200 compact controller", pns: ["6ES7 214-1AG40-0XB0"]);
        await _search.IndexProductsAsync([plc, Doc("Sinamics drive", pns: ["6SL3210-0KE17-0AB1"])]);

        var hit = await _search.SearchAsync(new SearchQuery { Text = "6ES7 214-1AG40-0XB0" });
        hit.Hits.First().Id.Should().Be(plc.Id);
    }

    [MeiliFact]
    public async Task TitleSearch_ToleratesATypo()
    {
        await _search.IndexProductsAsync([Doc("Frequency inverter 750 W")]);
        (await _search.SearchAsync(new SearchQuery { Text = "frequncy invertor" })).Hits.Should().HaveCount(1);
    }

    [MeiliFact]
    public async Task BengaliTitle_IsSearchable()
    {
        await _search.IndexProductsAsync([Doc("Servo motor", titleBn: "সার্ভো মোটর")]);
        (await _search.SearchAsync(new SearchQuery { Text = "সার্ভো" })).Hits.Should().HaveCount(1);
    }

    [MeiliFact]
    public async Task BrandCategoryStockAndPrice_Filter_AndFacetsCount()
    {
        await _search.IndexProductsAsync([
            Doc("Drive A", brand: "abb", category: "drives", price: 5_000),
            Doc("Drive B", brand: "abb", category: "drives", price: 50_000, inStock: false),
            Doc("Drive C", brand: "siemens", category: "drives", price: 20_000),
            Doc("Relay D", brand: "omron", category: "relays", price: 1_000),
        ]);

        var abb = await _search.SearchAsync(new SearchQuery { BrandSlugs = ["abb"] });
        abb.Hits.Select(h => h.TitleEn).Should().BeEquivalentTo("Drive A", "Drive B");

        var stocked = await _search.SearchAsync(new SearchQuery { CategorySlug = "drives", InStockOnly = true });
        stocked.Hits.Select(h => h.TitleEn).Should().BeEquivalentTo("Drive A", "Drive C");

        var priced = await _search.SearchAsync(new SearchQuery { MinPriceMinor = 10_000, MaxPriceMinor = 30_000 });
        priced.Hits.Should().ContainSingle().Which.TitleEn.Should().Be("Drive C");

        var all = await _search.SearchAsync(new SearchQuery());
        all.Total.Should().Be(4);
        all.Facets["brandSlug"]["abb"].Should().Be(2);
        all.Facets["categoryPath"]["drives"].Should().Be(3);
    }

    [MeiliFact]
    public async Task FilterInjection_NeverReachesTheEngine()
    {
        await _search.IndexProductsAsync([Doc("Drive A", brand: "abb"), Doc("Drive B", brand: "omron")]);

        var act = () => _search.SearchAsync(new SearchQuery { BrandSlugs = ["abb\" OR brandSlug != \"zzz"] });
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [MeiliFact]
    public async Task Paging_AndSorting_Work()
    {
        await _search.IndexProductsAsync(Enumerable.Range(1, 5).Select(i => Doc($"Part {i}", price: i * 1_000, sold: 10 - i)).ToList());

        var page2 = await _search.SearchAsync(new SearchQuery { Page = 2, PageSize = 2, Sort = SearchSort.PriceAscending });
        page2.Total.Should().Be(5);
        page2.Hits.Select(h => h.PriceMinor).Should().Equal(3_000, 4_000);

        var top = await _search.SearchAsync(new SearchQuery { PageSize = 1, Sort = SearchSort.PriceDescending });
        top.Hits.Single().PriceMinor.Should().Be(5_000);

        var sold = await _search.SearchAsync(new SearchQuery { PageSize = 1, Sort = SearchSort.MostSold });
        sold.Hits.Single().TitleEn.Should().Be("Part 1");
    }

    [MeiliFact]
    public async Task Autocomplete_FindsByPrefix_AndHonoursTheLimit()
    {
        await _search.IndexProductsAsync(Enumerable.Range(1, 8).Select(i => Doc($"Contactor {i}")).ToList());

        var result = await _search.AutocompleteAsync("conta", limit: 3);
        result.Suggestions.Should().HaveCount(3);
        result.Suggestions.Should().OnlyContain(s => s.TitleEn.StartsWith("Contactor") && s.Slug.StartsWith("contactor"));
    }

    [MeiliFact]
    public async Task Delete_RemovesFromTheIndex_AndReindexReplaces()
    {
        var a = Doc("Sensor A");
        var b = Doc("Sensor B");
        await _search.IndexProductsAsync([a, b]);

        await _search.DeleteProductsAsync([a.Id]);
        (await _search.SearchAsync(new SearchQuery { Text = "sensor" })).Hits.Should().ContainSingle().Which.Id.Should().Be(b.Id);

        await _search.IndexProductsAsync([b with { TitleEn = "Sensor B renamed" }]);
        var again = await _search.SearchAsync(new SearchQuery { Text = "sensor" });
        again.Hits.Should().ContainSingle().Which.TitleEn.Should().Be("Sensor B renamed");
    }

    [MeiliFact]
    public async Task Synonyms_LetOneWordFindTheOther()
    {
        await _search.IndexProductsAsync([Doc("Variable frequency drive")]);
        (await _search.SearchAsync(new SearchQuery { Text = "inverter" })).Hits.Should().BeEmpty();

        await _search.UpdateSynonymsAsync(new Dictionary<string, IReadOnlyList<string>> { ["inverter"] = ["drive"], ["drive"] = ["inverter"] });

        (await _search.SearchAsync(new SearchQuery { Text = "inverter" })).Hits.Should().HaveCount(1);
    }

    [MeiliFact]
    public async Task EmptyIndexOrDeleteBatches_AreHarmless()
    {
        await _search.IndexProductsAsync([]);
        await _search.DeleteProductsAsync([]);
        (await _search.SearchAsync(new SearchQuery { Text = "nothing" })).Hits.Should().BeEmpty();
    }

    [MeiliFact]
    public async Task EngineDown_BecomesA503_NotAnUnhandledError()
    {
        var down = Make(url: "http://127.0.0.1:1");
        var act = () => down.SearchAsync(new SearchQuery());
        (await act.Should().ThrowAsync<ServiceUnavailableException>()).Which.Code.Should().Be("SEARCH_UNAVAILABLE");
    }

    [MeiliFact]
    public async Task WrongKey_BecomesA503_AndTheEngineMessageIsNotReturned()
    {
        var wrong = Make(key: "not-the-key");
        var act = () => wrong.SearchAsync(new SearchQuery());
        var thrown = (await act.Should().ThrowAsync<ServiceUnavailableException>()).Which;
        thrown.Message.Should().NotContain("api key", because: "engine messages stay in the log");
        thrown.Message.Should().NotContain("401", because: "how the engine is set up is not the caller's business");
    }

    [MeiliFact]
    public async Task EngineTaskThatFails_BecomesA503_NotASilentSuccess()
    {
        // An index whose primary key our documents do not have: the engine accepts the request, then the task fails.
        var other = _index + "_pk";
        using var http = new HttpClient { BaseAddress = new Uri(MeiliInfra.Url + "/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", MeiliInfra.Key);
        (await http.PostAsync("indexes", new StringContent($$"""{"uid":"{{other}}","primaryKey":"sku"}""", System.Text.Encoding.UTF8, "application/json")))
            .EnsureSuccessStatusCode();
        try
        {
            await Task.Delay(500);
            var act = () => Make(index: other).IndexProductsAsync([Doc("No sku field")]);
            // Must be reported as a failed task straight away, not discovered later as a timeout.
            (await act.Should().ThrowAsync<ServiceUnavailableException>()).Which.Data["reason"].Should().BeOfType<string>()
                .Which.Should().Contain("did not succeed");
        }
        finally { await http.DeleteAsync($"indexes/{other}"); }
    }

    [MeiliFact]
    public async Task OverlongAutocompletePrefix_IsRefused()
    {
        var act = () => _search.AutocompleteAsync(new string('x', 201));
        await act.Should().ThrowAsync<ValidationException>();
    }
}
