using System.Net;
using System.Text.Json;
using FluentAssertions;
using Mechware.Application.Search;
using Mechware.Domain.Catalog;
using Mechware.Infrastructure.Search;
using Mechware.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Mechware.IntegrationTests.Search;

/// <summary>The public search API over a real PostgreSQL host and a throw-away Meilisearch index.</summary>
public sealed class SearchApiTests : IAsyncLifetime
{
    private const string PartNumber = "6ES7214-1AG40-0XB0";
    private const string SkuWithOIL = "OMR-PLC-24V";

    private readonly string _index = "api_test_" + Guid.NewGuid().ToString("N");
    private DbBackedFactory? _factory;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        if (!TestInfra.PostgresAvailable || !TestInfra.RedisAvailable || !MeiliInfra.Available) return;

        _factory = new DbBackedFactory(settings: new Dictionary<string, string>
        {
            ["Search:Url"] = MeiliInfra.Url,
            ["Search:ApiKey"] = MeiliInfra.Key,
            ["Search:IndexName"] = _index,
        });
        var provider = _factory.Services.GetRequiredService<ISearchProvider>();
        await provider.EnsureIndexAsync();
        await provider.UpdateSynonymsAsync(SearchSynonyms.Default);
        await provider.IndexProductsAsync(
        [
            new ProductSearchDocument
            {
                Id = Guid.NewGuid(), Slug = "gearbox-750", TitleEn = "Gearbox alpha", BrandName = "Zed", BrandSlug = "zed", PriceMinor = 100,
                Measures = new Dictionary<string, IReadOnlyList<double>> { ["power"] = [750] },
                NumericAttributes = new Dictionary<string, double> { ["power_kw"] = 0.75 },
            },
            new ProductSearchDocument
            {
                Id = Guid.NewGuid(), Slug = "gearbox-1500", TitleEn = "Gearbox beta", BrandName = "Zed", BrandSlug = "zed", PriceMinor = 100,
                Measures = new Dictionary<string, IReadOnlyList<double>> { ["power"] = [1500] },
                NumericAttributes = new Dictionary<string, double> { ["power_kw"] = 1.5 },
            },
            new ProductSearchDocument
            {
                Id = Guid.NewGuid(), Slug = "siemens-v20", TitleEn = "Siemens V20 inverter", BrandName = "Siemens", BrandSlug = "siemens", PriceMinor = 100,
            },
            new ProductSearchDocument
            {
                Id = Guid.NewGuid(), Slug = "siemens-6es7214", TitleEn = "Siemens S7-1200 CPU 1214C", BrandName = "Siemens", BrandSlug = "siemens",
                PartNumbers = [PartNumber], PartNumberKeys = [PartNumbers.Normalise(PartNumber), PartNumbers.Fuzzy(PartNumbers.Normalise(PartNumber))],
                PriceMinor = 5_000_000, InStock = true, SoldCount = 7,
            },
            new ProductSearchDocument
            {
                Id = Guid.NewGuid(), Slug = "omron-plc", TitleEn = "Omron PLC module", BrandName = "Omron", BrandSlug = "omron",
                PartNumbers = [SkuWithOIL], PartNumberKeys = [PartNumbers.Normalise(SkuWithOIL), PartNumbers.Fuzzy(PartNumbers.Normalise(SkuWithOIL))],
                PriceMinor = 1_000_000, InStock = true,
            },
            new ProductSearchDocument
            {
                Id = Guid.NewGuid(), Slug = "abb-acs580", TitleEn = "ABB ACS580 inverter", BrandName = "ABB", BrandSlug = "abb",
                PartNumbers = ["ACS580-01-12A6-4"], PartNumberKeys = ["ACS580011A64"], PriceMinor = 9_000_000, InStock = false,
            },
            new ProductSearchDocument
            {
                Id = Guid.NewGuid(), Slug = "siemens-sinamics", TitleEn = "Siemens SINAMICS G120 inverter", BrandName = "Siemens", BrandSlug = "siemens",
                PartNumbers = ["6SL3210-1KE12-3AF2"], PartNumberKeys = ["6SL32101KE123AF2"], PriceMinor = 7_000_000, InStock = true,
            },
        ]);
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        if (!MeiliInfra.Available) return;
        using var http = new HttpClient { BaseAddress = new Uri(MeiliInfra.Url + "/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", MeiliInfra.Key);
        await http.DeleteAsync($"indexes/{_index}");
    }

    private static async Task<(HttpResponseMessage Response, string Body, JsonDocument Json)> Read(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return (response, body, JsonDocument.Parse(body));
    }

    private static string[] Slugs(JsonDocument json) =>
        json.RootElement.GetProperty("data").EnumerateArray().Select(h => h.GetProperty("slug").GetString()!).ToArray();

    [SearchStackFact]
    public async Task PartNumber_TypedAnyWay_FindsTheProduct_FirstByKey()
    {
        var (response, _, json) = await Read(await _client.GetAsync("/api/v1/search?q=" + Uri.EscapeDataString("6es7 214-1ag40-0xb0")));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json.RootElement.GetProperty("meta").GetProperty("matchedBy").GetString().Should().Be("partNumber");
        Slugs(json).First().Should().Be("siemens-6es7214");
    }

    [SearchStackFact]
    public async Task PartNumber_WithLetterOForZero_StillFindsIt()
    {
        var (response, _, json) = await Read(await _client.GetAsync("/api/v1/search?q=" + Uri.EscapeDataString("6ES7214-1AG4O-OXBO")));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json.RootElement.GetProperty("meta").GetProperty("matchedBy").GetString().Should().Be("partNumber");
        Slugs(json).Should().Contain("siemens-6es7214");
    }

    [SearchStackFact]
    public async Task FreeText_FindsByWords_AndHidesIndexInternals()
    {
        var (response, body, json) = await Read(await _client.GetAsync("/api/v1/search?q=inverter"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json.RootElement.GetProperty("meta").GetProperty("matchedBy").GetString().Should().Be("text");
        Slugs(json).Should().Contain("abb-acs580");
        body.Should().NotContain("partNumberKeys").And.NotContain("soldCount");
    }

    [SearchStackFact]
    public async Task BrandFilter_OnlyReturnsThatBrand_AndTheAnswerIsPubliclyCacheable()
    {
        var response = await _client.GetAsync("/api/v1/search?q=inverter&brand=abb");
        var (_, _, json) = await Read(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.ToString().Should().Contain("public");
        json.RootElement.GetProperty("data").EnumerateArray().Select(h => h.GetProperty("brandSlug").GetString())
            .Should().NotBeEmpty().And.OnlyContain(b => b == "abb");
    }

    [SearchStackTheory]
    [InlineData("pageSize=500")]
    [InlineData("sort=bogus")]
    [InlineData("brand=Bad%20Brand")]
    [InlineData("category=a%22b")]
    public async Task BadInput_Is400_ValidationError(string query)
    {
        var (response, body, _) = await Read(await _client.GetAsync("/api/v1/search?q=x&" + query));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().Contain("VALIDATION_ERROR");
    }

    [SearchStackTheory]
    [InlineData("750 W")]
    [InlineData("0.75kW")]
    public async Task MeasureInText_FindsTheProductWithThatPower_InAnyUnit(string q)
    {
        var (response, _, json) = await Read(await _client.GetAsync("/api/v1/search?q=" + Uri.EscapeDataString(q)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json.RootElement.GetProperty("meta").GetProperty("matchedBy").GetString().Should().Be("measure");
        Slugs(json).Should().Equal("gearbox-750");
    }

    [SearchStackFact]
    public async Task MeasureOutsideHalfAPercent_FallsBackToText()
    {
        var (response, _, json) = await Read(await _client.GetAsync("/api/v1/search?q=" + Uri.EscapeDataString("1 HP")));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json.RootElement.GetProperty("meta").GetProperty("matchedBy").GetString().Should().Be("text");
        Slugs(json).Should().NotContain("gearbox-750");
    }

    [SearchStackFact]
    public async Task AttributeRange_FiltersByNumericAttributes()
    {
        var (response, _, json) = await Read(await _client.GetAsync("/api/v1/search?q=gearbox&attr=" + Uri.EscapeDataString("power_kw:0.5..1")));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Slugs(json).Should().Equal("gearbox-750");

        (_, _, json) = await Read(await _client.GetAsync("/api/v1/search?q=gearbox&attr=" + Uri.EscapeDataString("power_kw:1..")));
        Slugs(json).Should().Equal("gearbox-1500");
    }

    [SearchStackTheory]
    [InlineData("bad code:1..2")]
    [InlineData("power_kw:abc..")]
    public async Task BadAttribute_Is400(string attr)
    {
        var (response, body, _) = await Read(await _client.GetAsync("/api/v1/search?q=x&attr=" + Uri.EscapeDataString(attr)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().Contain("VALIDATION_ERROR");
    }

    [SearchStackTheory]
    [InlineData("vfd")]
    [InlineData("invertar")]
    public async Task Synonyms_FindTheInverter(string q)
    {
        var (response, _, json) = await Read(await _client.GetAsync("/api/v1/search?q=" + q));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Slugs(json).Should().Contain("siemens-v20");
    }

    [SearchStackFact]
    public async Task Autocomplete_SuggestsMatchingProducts()
    {
        var (response, _, json) = await Read(await _client.GetAsync("/api/v1/search/autocomplete?q=inv"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Slugs(json).Should().Contain("abb-acs580");
    }

    [SearchStackFact]
    public async Task Autocomplete_WithoutText_Is400()
    {
        var (response, body, _) = await Read(await _client.GetAsync("/api/v1/search/autocomplete?q="));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().Contain("VALIDATION_ERROR");
    }

    [SearchStackFact]
    public async Task EngineDown_Is503_WithoutLeakingTheAddress()
    {
        using var down = new DbBackedFactory(settings: new Dictionary<string, string>
        {
            ["Search:Url"] = "http://127.0.0.1:9",
            ["Search:ApiKey"] = "x",
            ["Search:IndexName"] = _index,
        });
        using var client = down.CreateClient();

        var (response, body, _) = await Read(await client.GetAsync("/api/v1/search?q=x"));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().Contain("SEARCH_UNAVAILABLE").And.NotContain("127.0.0.1").And.NotContain("unreachable").And.NotContain("401");
    }

    [SearchStackFact]
    public async Task SkuContainingOIL_IsFoundAsAPartNumber_AsTypedAndWithDigitsForLetters()
    {
        foreach (var typed in new[] { "omr-plc-24v", "0MR-P1C-24V" })
        {
            var (response, _, json) = await Read(await _client.GetAsync("/api/v1/search?q=" + Uri.EscapeDataString(typed)));
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            json.RootElement.GetProperty("meta").GetProperty("matchedBy").GetString().Should().Be("partNumber");
            Slugs(json).First().Should().Be("omron-plc");
        }
    }

    [SearchStackFact]
    public async Task AnonymousBrowsing_IsNotThrottledByTheGlobalOrIpLimiters()
    {
        using var strict = new DbBackedFactory(settings: new Dictionary<string, string>
        {
            ["Search:Url"] = MeiliInfra.Url,
            ["Search:ApiKey"] = MeiliInfra.Key,
            ["Search:IndexName"] = _index,
            ["RateLimiting:GlobalPermitPerMinute"] = "100",
            ["RateLimiting:IpCeilingPerMinute"] = "100",
        });
        using var client = strict.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 120; i++) statuses.Add((await client.GetAsync("/api/v1/search?q=x")).StatusCode);

        statuses.Should().NotContain(HttpStatusCode.TooManyRequests);
    }
}

/// <summary><see cref="SearchStackFactAttribute"/> for theories.</summary>
public sealed class SearchStackTheoryAttribute : TheoryAttribute
{
    public SearchStackTheoryAttribute()
    {
        if (!TestInfra.PostgresAvailable) Skip = "PostgreSQL not reachable (set MECHWARE_TEST_PG).";
        else if (!TestInfra.RedisAvailable) Skip = "Redis not reachable (set MECHWARE_TEST_REDIS).";
        else if (!MeiliInfra.Available) Skip = "Meilisearch not reachable (set MECHWARE_TEST_MEILI).";
    }
}
