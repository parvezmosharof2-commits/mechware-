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

        public Task<SearchResultDto<ProductSearchDocument>> SearchAsync(SearchQuery query, CancellationToken ct = default)
        {
            Queries.Add(query);
            var total = query.PartNumberKey is null ? TextTotal : KeyTotal;
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
