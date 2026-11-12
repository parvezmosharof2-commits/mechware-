using Mechware.Domain.Catalog;

namespace Mechware.Application.Search;

/// <summary>
/// One product as the search index sees it. Only <c>Active</c> products are indexed (the indexer deletes the rest), so a hit
/// is always something the storefront may show. The index is a rebuildable copy: PostgreSQL stays the source of truth.
/// </summary>
public sealed record ProductSearchDocument
{
    /// <summary>Product id; also the index primary key.</summary>
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string TitleEn { get; init; }
    public string TitleBn { get; init; } = "";
    public string BrandName { get; init; } = "";
    public string BrandSlug { get; init; } = "";
    /// <summary>Slugs of the category and all its ancestors, so a filter on any ancestor finds the product.</summary>
    public IReadOnlyList<string> CategoryPath { get; init; } = [];
    /// <summary>Raw manufacturer part numbers and SKUs of every variant, as written (free-text search).</summary>
    public IReadOnlyList<string> PartNumbers { get; init; } = [];
    /// <summary>
    /// Normalised and fuzzy keys (<c>PartNumbers.Normalise</c> / <c>Fuzzy</c>) of every variant. Exact part-number lookup filters on
    /// these instead of relying on the engine's tokenizer, which splits part numbers into pieces.
    /// </summary>
    public IReadOnlyList<string> PartNumberKeys { get; init; } = [];
    /// <summary>Lowest variant price in poisha (minor units); 0 when no variant has a price.</summary>
    public long PriceMinor { get; init; }
    public string Currency { get; init; } = "BDT";
    public bool InStock { get; init; }
    /// <summary><c>active</c>, <c>nrnd</c> or <c>obsolete</c>.</summary>
    public string SupplyStatus { get; init; } = "active";
    public double RatingAvg { get; init; }
    public int SoldCount { get; init; }
    /// <summary>Numeric attributes by code, each in its own unit (see <c>Units</c>). Filtering on them arrives with the facet step.</summary>
    public IReadOnlyDictionary<string, double> NumericAttributes { get; init; } = new Dictionary<string, double>();
    /// <summary>
    /// Measures by quantity (<c>power</c>, <c>voltage</c>, ...), each value converted to the quantity's base unit (W, V, A, mm, Pa, Hz, %),
    /// so "750 W" in free text finds a product whose power is stored as 0.75 kW. Range attributes are not included.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<double>> Measures { get; init; } = new Dictionary<string, IReadOnlyList<double>>();
}

public enum SearchSort { Relevance = 0, PriceAscending = 1, PriceDescending = 2, MostSold = 3 }

/// <summary>A storefront search request. Every value is validated by <see cref="SearchQuery.Validate"/> before it reaches the index.</summary>
public sealed record SearchQuery
{
    public const int MaxTextLength = 200;
    public const int MaxPageSize = 50;
    public const int MaxPage = 200;
    public const int MaxBrands = 20;
    public const int MaxAttributeRanges = 10;

    public string Text { get; init; } = "";
    /// <summary>Exact part-number key (already normalised by the caller: A–Z and 0–9 only). Matches normalised or fuzzy keys.</summary>
    public string? PartNumberKey { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 24;
    public IReadOnlyList<string> BrandSlugs { get; init; } = [];
    public string? CategorySlug { get; init; }
    public bool InStockOnly { get; init; }
    public long? MinPriceMinor { get; init; }
    public long? MaxPriceMinor { get; init; }
    public SearchSort Sort { get; init; } = SearchSort.Relevance;
    /// <summary>Quantity of a measure filter (see <c>UnitCatalog.Quantities</c>); the bounds are in that quantity's base unit.</summary>
    public string? MeasureQuantity { get; init; }
    public double? MeasureMin { get; init; }
    public double? MeasureMax { get; init; }
    /// <summary>Range filters on numeric attributes, each in the attribute's stored unit.</summary>
    public IReadOnlyList<AttributeRange> AttributeRanges { get; init; } = [];

    /// <summary>Returns the problems with this request, or an empty list when it is acceptable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Text.Length > MaxTextLength) problems.Add($"text is longer than {MaxTextLength} characters");
        if (Page < 1 || Page > MaxPage) problems.Add($"page must be between 1 and {MaxPage}");
        if (PageSize < 1 || PageSize > MaxPageSize) problems.Add($"pageSize must be between 1 and {MaxPageSize}");
        if (BrandSlugs.Count > MaxBrands) problems.Add($"at most {MaxBrands} brands");
        if (BrandSlugs.Any(b => !SearchSlug.IsValid(b))) problems.Add("brand slug has characters that are not allowed");
        if (CategorySlug is not null && !SearchSlug.IsValid(CategorySlug)) problems.Add("category slug has characters that are not allowed");
        if (PartNumberKey is not null && !IsPartNumberKey(PartNumberKey)) problems.Add("part-number key must be 2–64 characters of A–Z and 0–9");
        if (MinPriceMinor is < 0) problems.Add("minimum price cannot be negative");
        if (MaxPriceMinor is < 0) problems.Add("maximum price cannot be negative");
        if (MinPriceMinor is not null && MaxPriceMinor is not null && MinPriceMinor > MaxPriceMinor) problems.Add("minimum price is above the maximum");
        if (!Enum.IsDefined(Sort)) problems.Add("unknown sort");
        if (MeasureQuantity is null)
        {
            if (MeasureMin is not null || MeasureMax is not null) problems.Add("measure bounds need a quantity");
        }
        else
        {
            if (!UnitCatalog.Quantities.Contains(MeasureQuantity)) problems.Add("unknown measure quantity");
            if (MeasureMin is null && MeasureMax is null) problems.Add("measure needs a minimum or a maximum");
            if (!IsFiniteOrNull(MeasureMin) || !IsFiniteOrNull(MeasureMax)) problems.Add("measure bounds must be finite numbers");
            else if (MeasureMin > MeasureMax) problems.Add("measure minimum is above the maximum");
        }
        if (AttributeRanges.Count > MaxAttributeRanges) problems.Add($"at most {MaxAttributeRanges} attribute ranges");
        foreach (var r in AttributeRanges)
        {
            if (!SearchAttributeCode.IsValid(r.Code)) problems.Add("attribute code has characters that are not allowed");
            if (r.Min is null && r.Max is null) problems.Add("attribute range needs a minimum or a maximum");
            if (!IsFiniteOrNull(r.Min) || !IsFiniteOrNull(r.Max)) problems.Add("attribute bounds must be finite numbers");
            else if (r.Min > r.Max) problems.Add("attribute minimum is above the maximum");
        }
        return problems;
    }

    private static bool IsFiniteOrNull(double? v) => v is null || double.IsFinite(v.Value);

    private static bool IsPartNumberKey(string key) =>
        key.Length is >= 2 and <= 64 && key.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9');
}

/// <summary>A numeric-attribute range filter; either bound may be missing.</summary>
public sealed record AttributeRange(string Code, double? Min, double? Max);

/// <summary>The attribute-code alphabet (<c>^[a-z0-9_]{1,64}$</c>). Anything else is refused, never escaped.</summary>
public static class SearchAttributeCode
{
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64) return false;
        foreach (var c in value)
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')) return false;
        return true;
    }
}

/// <summary>The slug alphabet. Slugs are lower-case letters, digits and dashes; anything else is refused, never escaped.</summary>
public static class SearchSlug
{
    public const int MaxLength = 120;

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength) return false;
        foreach (var c in value)
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) return false;
        return true;
    }
}

public sealed record SearchResultDto<T>(
    IReadOnlyList<T> Hits, long Total, int Page, int PageSize, int ProcessingMs,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> Facets);

public sealed record AutocompleteSuggestion(Guid ProductId, string Slug, string TitleEn, string TitleBn);

public sealed record AutocompleteResultDto(IReadOnlyList<AutocompleteSuggestion> Suggestions, int ProcessingMs);

/// <summary>
/// Search port (ADR-0005). The storefront talks to this, never to a concrete engine. Indexing is driven by the outbox, never
/// from a controller.
/// </summary>
public interface ISearchProvider
{
    /// <summary>Creates the index and applies its settings when needed. Safe to call on every start.</summary>
    Task EnsureIndexAsync(CancellationToken ct = default);

    Task<SearchResultDto<ProductSearchDocument>> SearchAsync(SearchQuery query, CancellationToken ct = default);

    Task<AutocompleteResultDto> AutocompleteAsync(string prefix, int limit = 5, CancellationToken ct = default);

    /// <summary>Adds or replaces documents (same id replaces).</summary>
    Task IndexProductsAsync(IReadOnlyCollection<ProductSearchDocument> documents, CancellationToken ct = default);

    Task DeleteProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default);

    /// <summary>
    /// Rebuilds the whole index from <paramref name="batches"/> without an empty window: documents go into a fresh index,
    /// which then replaces the live one in one swap. Settings and synonyms carry over; documents not in the batches disappear.
    /// </summary>
    Task ReplaceAllAsync(IAsyncEnumerable<IReadOnlyCollection<ProductSearchDocument>> batches, CancellationToken ct = default);

    /// <summary>Replaces the whole synonym set. Each key maps to the words that mean the same thing.</summary>
    Task UpdateSynonymsAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> synonyms, CancellationToken ct = default);
}
