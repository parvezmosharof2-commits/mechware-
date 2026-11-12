using FluentAssertions;
using Mechware.Application.Search;
using Mechware.Domain.Catalog;
using Mechware.Domain.Common.Exceptions;
using Mechware.Infrastructure.Search;

namespace Mechware.UnitTests.Search;

public class StorefrontSearchServiceTests
{
    private sealed class FakeProvider : ISearchProvider
    {
        public List<SearchQuery> Queries { get; } = [];
        public List<(string Prefix, int Limit)> Autocompletes { get; } = [];
        /// <summary>Total returned for a query that carries a part-number key.</summary>
        public long KeyTotal { get; set; }
        /// <summary>Total returned for any other query.</summary>
        public long TextTotal { get; set; } = 1;
        /// <summary>Total returned for a query that carries a measure filter.</summary>
        public long MeasureTotal { get; set; }

        public Task<SearchResultDto<ProductSearchDocument>> SearchAsync(SearchQuery query, CancellationToken ct = default)
        {
            Queries.Add(query);
            var total = query.PartNumberKey is not null ? KeyTotal : query.MeasureQuantity is not null ? MeasureTotal : TextTotal;
            return Task.FromResult(new SearchResultDto<ProductSearchDocument>(
                [], total, query.Page, query.PageSize, 1, new Dictionary<string, IReadOnlyDictionary<string, long>>()));
        }

        public Task<AutocompleteResultDto> AutocompleteAsync(string prefix, int limit = 5, CancellationToken ct = default)
        {
            Autocompletes.Add((prefix, limit));
            return Task.FromResult(new AutocompleteResultDto([], 1));
        }

        public Task EnsureIndexAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task IndexProductsAsync(IReadOnlyCollection<ProductSearchDocument> documents, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReplaceAllAsync(IAsyncEnumerable<IReadOnlyCollection<ProductSearchDocument>> batches, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateSynonymsAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> synonyms, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private const string PartNumberText = "6es7 214-1ag40-0xb0";

    private readonly FakeProvider _provider = new();
    private StorefrontSearchService Service => new(_provider);

    [Fact]
    public async Task PartNumberText_SearchesByKeyFirst_AndReportsPartNumber()
    {
        _provider.KeyTotal = 1;

        var result = await Service.SearchAsync(new StorefrontSearchRequest { Text = PartNumberText });

        _provider.Queries.Should().ContainSingle();
        _provider.Queries[0].PartNumberKey.Should().Be(PartNumbers.Fuzzy(PartNumbers.Normalise(PartNumberText)));
        _provider.Queries[0].PartNumberKey.Should().Be("6ES72141AG400XB0");
        _provider.Queries[0].Text.Should().BeEmpty();
        result.MatchedBy.Should().Be("partNumber");
    }

    [Fact]
    public async Task PartNumberText_WithLookAlikeLetters_IsSentAsTheFuzzyKey()
    {
        _provider.KeyTotal = 1;

        await Service.SearchAsync(new StorefrontSearchRequest { Text = "e3z-ls6l" });

        _provider.Queries[0].PartNumberKey.Should().Be("E3Z1S61", "O→0 and I/L→1, so the stored fuzzy key matches");
    }

    [Fact]
    public async Task PartNumberText_WithNoKeyMatch_FallsBackToFreeText()
    {
        _provider.KeyTotal = 0;

        var result = await Service.SearchAsync(new StorefrontSearchRequest { Text = PartNumberText });

        _provider.Queries.Should().HaveCount(2);
        _provider.Queries[1].Text.Should().Be(PartNumberText);
        _provider.Queries[1].PartNumberKey.Should().BeNull();
        result.MatchedBy.Should().Be("text");
    }

    [Fact]
    public async Task PlainWords_AreOneFreeTextCall()
    {
        var result = await Service.SearchAsync(new StorefrontSearchRequest { Text = "siemens inverter" });

        _provider.Queries.Should().ContainSingle();
        _provider.Queries[0].PartNumberKey.Should().BeNull();
        _provider.Queries[0].Text.Should().Be("siemens inverter");
        result.MatchedBy.Should().Be("text");
    }

    [Fact]
    public async Task FiltersPageAndSort_AreCarriedIntoBothCalls()
    {
        _provider.KeyTotal = 0;
        var request = new StorefrontSearchRequest
        {
            Text = PartNumberText,
            Page = 3,
            PageSize = 10,
            Brands = ["siemens", "abb"],
            Category = "plcs",
            InStockOnly = true,
            MinPriceMinor = 100,
            MaxPriceMinor = 5000,
            Sort = "price-asc",
        };

        await Service.SearchAsync(request);

        _provider.Queries.Should().HaveCount(2);
        foreach (var q in _provider.Queries)
        {
            q.Page.Should().Be(3);
            q.PageSize.Should().Be(10);
            q.BrandSlugs.Should().Equal("siemens", "abb");
            q.CategorySlug.Should().Be("plcs");
            q.InStockOnly.Should().BeTrue();
            q.MinPriceMinor.Should().Be(100);
            q.MaxPriceMinor.Should().Be(5000);
            q.Sort.Should().Be(SearchSort.PriceAscending);
        }
    }

    [Fact]
    public async Task MeasureInText_SearchesByMeasureWithin05Percent_AndReportsMeasure()
    {
        _provider.MeasureTotal = 1;

        var result = await Service.SearchAsync(new StorefrontSearchRequest { Text = "inverter 0.75 kW" });

        _provider.Queries.Should().ContainSingle();
        var q = _provider.Queries[0];
        q.Text.Should().Be("inverter");
        q.MeasureQuantity.Should().Be("power");
        q.MeasureMin.Should().BeApproximately(746.25, 0.001);
        q.MeasureMax.Should().BeApproximately(753.75, 0.001);
        result.MatchedBy.Should().Be("measure");
    }

    [Fact]
    public async Task MeasureWithNoHit_FallsBackToTheOriginalText()
    {
        _provider.MeasureTotal = 0;

        var result = await Service.SearchAsync(new StorefrontSearchRequest { Text = "1 HP" });

        _provider.Queries.Should().HaveCount(2);
        _provider.Queries[1].Text.Should().Be("1 HP");
        _provider.Queries[1].MeasureQuantity.Should().BeNull();
        result.MatchedBy.Should().Be("text");
    }

    [Fact]
    public async Task PartNumberComesBeforeMeasure()
    {
        _provider.KeyTotal = 1;

        var result = await Service.SearchAsync(new StorefrontSearchRequest { Text = "6es7 214-1ag40-0xb0" });

        result.MatchedBy.Should().Be("partNumber");
        _provider.Queries.Should().ContainSingle();
    }

    [Fact]
    public async Task Attributes_AreParsedIntoRanges_InEveryCall()
    {
        _provider.MeasureTotal = 0;

        await Service.SearchAsync(new StorefrontSearchRequest { Text = "750 W", Attributes = ["power_kw:0.5..", "voltage_v:..400", "x:1..2"] });

        foreach (var q in _provider.Queries)
            q.AttributeRanges.Should().Equal(new AttributeRange("power_kw", 0.5, null), new AttributeRange("voltage_v", null, 400), new AttributeRange("x", 1, 2));
    }

    [Theory]
    [InlineData("bad code:1..2")]
    [InlineData("power_kw:abc..")]
    [InlineData("power_kw:1")]
    [InlineData("power_kw")]
    [InlineData("power_kw:..")]
    [InlineData("power_kw:5..1")]
    [InlineData("power_kw:NaN..")]
    public async Task BadAttribute_IsAValidationError(string attr)
    {
        var act = () => Service.SearchAsync(new StorefrontSearchRequest { Text = "x", Attributes = [attr] });

        await act.Should().ThrowAsync<ValidationException>();
        _provider.Queries.Should().BeEmpty();
    }

    public static TheoryData<StorefrontSearchRequest> BadRequests => new()
    {
        new StorefrontSearchRequest { Text = "x", Sort = "bogus" },
        new StorefrontSearchRequest { Text = "x", PageSize = 0 },
        new StorefrontSearchRequest { Text = "x", PageSize = 51 },
        new StorefrontSearchRequest { Text = "x", Page = 0 },
        new StorefrontSearchRequest { Text = "x", Brands = ["Bad Brand"] },
        new StorefrontSearchRequest { Text = "x", MinPriceMinor = 500, MaxPriceMinor = 100 },
        new StorefrontSearchRequest { Text = new string('a', 201) },
    };

    [Theory]
    [MemberData(nameof(BadRequests))]
    public async Task BadInput_IsAValidationError_AndNeverReachesTheProvider(StorefrontSearchRequest request)
    {
        var act = () => Service.SearchAsync(request);

        await act.Should().ThrowAsync<ValidationException>();
        _provider.Queries.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Autocomplete_WithoutText_IsAValidationError(string? q)
    {
        var act = () => Service.AutocompleteAsync(q);

        await act.Should().ThrowAsync<ValidationException>();
        _provider.Autocompletes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public async Task Autocomplete_LimitOutOfRange_IsAValidationError(int limit)
    {
        var act = () => Service.AutocompleteAsync("inv", limit);

        await act.Should().ThrowAsync<ValidationException>();
        _provider.Autocompletes.Should().BeEmpty();
    }

    [Fact]
    public async Task Autocomplete_Valid_CallsTheProviderWithTrimmedText()
    {
        await Service.AutocompleteAsync("  inv  ", 7);

        _provider.Autocompletes.Should().ContainSingle().Which.Should().Be(("inv", 7));
    }
}
