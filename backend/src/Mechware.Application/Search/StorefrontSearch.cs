namespace Mechware.Application.Search;

/// <summary>A storefront search as the API receives it: raw query-string values, validated by <see cref="IStorefrontSearch"/>.</summary>
public sealed record StorefrontSearchRequest
{
    public string? Text { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 24;
    public IReadOnlyList<string> Brands { get; init; } = [];
    public string? Category { get; init; }
    public bool InStockOnly { get; init; }
    public long? MinPriceMinor { get; init; }
    public long? MaxPriceMinor { get; init; }
    /// <summary><c>relevance</c> (default), <c>price-asc</c>, <c>price-desc</c> or <c>most-sold</c>.</summary>
    public string? Sort { get; init; }
    /// <summary>Numeric attribute filters, each <c>code:min..max</c> (either side may be empty), in the attribute's stored unit.</summary>
    public IReadOnlyList<string> Attributes { get; init; } = [];
}

/// <summary>One product in a search answer. Only what a listing shows; index-internal fields (keys, sales counts) stay inside.</summary>
public sealed record StorefrontSearchHit(
    Guid Id, string Slug, string TitleEn, string TitleBn, string BrandName, string BrandSlug,
    IReadOnlyList<string> PartNumbers, long PriceMinor, string Currency, bool InStock, string SupplyStatus, double RatingAvg);

/// <summary>How the hits were found: <c>partNumber</c> (exact key, any formatting) or <c>measure</c> (a value with a unit in the text, e.g. "750 W") or <c>text</c> (free-text search).</summary>
public sealed record StorefrontSearchResult(
    IReadOnlyList<StorefrontSearchHit> Hits, long Total, int Page, int PageSize, string MatchedBy, int ProcessingMs,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> Facets);

/// <summary>
/// The storefront's search (C6.3). Text that looks like a part number is first looked up by its key, so
/// "6es7 214-1ag40-0xb0" finds 6ES7214-1AG40-0XB0 whatever the tokenizer does; free text is the fallback.
/// Bad input is a <c>ValidationException</c> (400); an engine failure a <c>ServiceUnavailableException</c> (503).
/// </summary>
public interface IStorefrontSearch
{
    public const int MaxAutocompleteLimit = 10;

    Task<StorefrontSearchResult> SearchAsync(StorefrontSearchRequest request, CancellationToken ct = default);

    Task<AutocompleteResultDto> AutocompleteAsync(string? prefix, int limit = 5, CancellationToken ct = default);
}
