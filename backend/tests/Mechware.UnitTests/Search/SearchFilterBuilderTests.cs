using FluentAssertions;
using Mechware.Application.Search;
using Mechware.Infrastructure.Search;

namespace Mechware.UnitTests.Search;

public class SearchFilterBuilderTests
{
    private static SearchQuery Q(Func<SearchQuery, SearchQuery>? change = null) => change?.Invoke(new SearchQuery()) ?? new SearchQuery();

    [Fact]
    public void NoFilters_GiveNoFilterExpression() => SearchFilterBuilder.Build(Q()).Should().BeNull();

    [Fact]
    public void Brands_AreOrTogether_AndQuoted()
    {
        SearchFilterBuilder.Build(Q(q => q with { BrandSlugs = ["siemens", "omron"] }))
            .Should().Be("(brandSlug = \"siemens\" OR brandSlug = \"omron\")");
    }

    [Fact]
    public void DuplicateBrands_AppearOnce()
    {
        SearchFilterBuilder.Build(Q(q => q with { BrandSlugs = ["abb", "abb"] })).Should().Be("(brandSlug = \"abb\")");
    }

    [Fact]
    public void AllFilters_AreAndedInAFixedOrder()
    {
        var filter = SearchFilterBuilder.Build(new SearchQuery
        {
            BrandSlugs = ["abb"],
            CategorySlug = "drives",
            InStockOnly = true,
            MinPriceMinor = 100,
            MaxPriceMinor = 9_000,
        });
        filter.Should().Be("(brandSlug = \"abb\") AND categoryPath = \"drives\" AND inStock = true AND priceMinor >= 100 AND priceMinor <= 9000");
    }

    [Theory]
    [InlineData("abb\" OR inStock = true OR brandSlug = \"x")]
    [InlineData("abb\\")]
    [InlineData("ABB")]
    [InlineData("abb ")]
    [InlineData("")]
    [InlineData("a_b")]
    [InlineData("abb)")]
    public void BrandSlugOutsideTheAlphabet_IsRefused_NotEscaped(string slug)
    {
        var act = () => SearchFilterBuilder.Build(Q(q => q with { BrandSlugs = [slug] }));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void PartNumberKey_IsAnExactFilterAndComesFirst()
    {
        SearchFilterBuilder.Build(Q(q => q with { PartNumberKey = "6ES72141AG400XB0", InStockOnly = true }))
            .Should().Be("partNumberKeys = \"6ES72141AG400XB0\" AND inStock = true");
    }

    [Theory]
    [InlineData("6es72141")]
    [InlineData("6ES7 2141")]
    [InlineData("6ES7\" OR inStock = true OR partNumberKeys = \"X")]
    [InlineData("A")]
    [InlineData("৬ES7")]
    public void PartNumberKeyThatIsNotNormalised_IsRefused(string key)
    {
        var act = () => SearchFilterBuilder.Build(Q(q => q with { PartNumberKey = key }));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void PartNumberKey_LongerThan64_IsRefused() =>
        new SearchQuery { PartNumberKey = new string('A', 65) }.Validate().Should().NotBeEmpty();

    [Theory]
    [InlineData("drives\" OR priceMinor >= 0 OR categoryPath = \"x")]
    [InlineData("Drives")]
    [InlineData("")]
    public void CategorySlugOutsideTheAlphabet_IsRefused(string slug)
    {
        var act = () => SearchFilterBuilder.Build(Q(q => q with { CategorySlug = slug }));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Prices_AreWrittenWithoutCultureFormatting()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("bn-BD");
            SearchFilterBuilder.Build(Q(q => q with { MinPriceMinor = 1_234_567 })).Should().Be("priceMinor >= 1234567");
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(SearchSort.Relevance, null)]
    [InlineData(SearchSort.PriceAscending, "priceMinor:asc")]
    [InlineData(SearchSort.PriceDescending, "priceMinor:desc")]
    [InlineData(SearchSort.MostSold, "soldCount:desc")]
    public void Sort_MapsToTheEngineSortExpression(SearchSort sort, string? expected)
    {
        var result = SearchFilterBuilder.BuildSort(sort);
        if (expected is null) result.Should().BeNull(); else result.Should().Equal(expected);
    }
}

public class SearchQueryValidationTests
{
    [Fact]
    public void DefaultQuery_IsValid() => new SearchQuery().Validate().Should().BeEmpty();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public void PageOutOfRange_IsRefused(int page) => new SearchQuery { Page = page }.Validate().Should().NotBeEmpty();

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    [InlineData(int.MaxValue)]
    public void PageSizeOutOfRange_IsRefused(int size) => new SearchQuery { PageSize = size }.Validate().Should().NotBeEmpty();

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    public void PageSizeAtTheEdges_IsAccepted(int size) => new SearchQuery { PageSize = size }.Validate().Should().BeEmpty();

    [Fact]
    public void TextAboveTheLimit_IsRefused() => new SearchQuery { Text = new string('x', 201) }.Validate().Should().NotBeEmpty();

    [Fact]
    public void TextAtTheLimit_IsAccepted() => new SearchQuery { Text = new string('x', 200) }.Validate().Should().BeEmpty();

    [Fact]
    public void TooManyBrands_AreRefused() =>
        new SearchQuery { BrandSlugs = Enumerable.Range(0, 21).Select(i => $"b{i}").ToList() }.Validate().Should().NotBeEmpty();

    [Fact]
    public void NegativeOrInvertedPrices_AreRefused()
    {
        new SearchQuery { MinPriceMinor = -1 }.Validate().Should().NotBeEmpty();
        new SearchQuery { MaxPriceMinor = -1 }.Validate().Should().NotBeEmpty();
        new SearchQuery { MinPriceMinor = 10, MaxPriceMinor = 5 }.Validate().Should().NotBeEmpty();
    }

    [Fact]
    public void UnknownSort_IsRefused() => new SearchQuery { Sort = (SearchSort)99 }.Validate().Should().NotBeEmpty();

    [Fact]
    public void SlugAlphabet_AcceptsOnlyLowerCaseDigitsAndDash()
    {
        SearchSlug.IsValid("vfd-750w").Should().BeTrue();
        SearchSlug.IsValid(null).Should().BeFalse();
        SearchSlug.IsValid(new string('a', 121)).Should().BeFalse();
        SearchSlug.IsValid("é").Should().BeFalse();
    }
}
