using Mechware.Application.Search;
using Mechware.Domain.Catalog;
using Mechware.Domain.Common.Exceptions;

namespace Mechware.Infrastructure.Search;

/// <inheritdoc cref="IStorefrontSearch"/>
public sealed class StorefrontSearchService(ISearchProvider search) : IStorefrontSearch
{
    public const string MatchedByPartNumber = "partNumber";
    public const string MatchedByText = "text";

    private static readonly Dictionary<string, SearchSort> Sorts = new(StringComparer.Ordinal)
    {
        ["relevance"] = SearchSort.Relevance,
        ["price-asc"] = SearchSort.PriceAscending,
        ["price-desc"] = SearchSort.PriceDescending,
        ["most-sold"] = SearchSort.MostSold,
    };

    public async Task<StorefrontSearchResult> SearchAsync(StorefrontSearchRequest request, CancellationToken ct = default)
    {
        var query = ToQuery(request);

        if (PartNumbers.LooksLikePartNumber(query.Text))
        {
            // The fuzzy form of the input matches both keys stored per variant (normalised and fuzzy), so O/0 and I/L/1 mix-ups hit too.
            var key = PartNumbers.Fuzzy(PartNumbers.Normalise(query.Text));
            var byKey = await search.SearchAsync(query with { Text = "", PartNumberKey = key }, ct);
            if (byKey.Total > 0) return Map(byKey, MatchedByPartNumber);
        }

        return Map(await search.SearchAsync(query, ct), MatchedByText);
    }

    public Task<AutocompleteResultDto> AutocompleteAsync(string? prefix, int limit = 5, CancellationToken ct = default)
    {
        var text = prefix?.Trim() ?? "";
        var errors = new List<ValidationError>();
        if (text.Length == 0) errors.Add(new("q", "Type at least one character."));
        if (text.Length > SearchQuery.MaxTextLength) errors.Add(new("q", $"At most {SearchQuery.MaxTextLength} characters."));
        if (limit is < 1 or > IStorefrontSearch.MaxAutocompleteLimit)
            errors.Add(new("limit", $"limit must be between 1 and {IStorefrontSearch.MaxAutocompleteLimit}."));
        if (errors.Count > 0) throw new ValidationException(errors);
        return search.AutocompleteAsync(text, limit, ct);
    }

    /// <summary>Turns the raw request into a <see cref="SearchQuery"/>, or throws one <see cref="ValidationException"/> listing every problem.</summary>
    internal static SearchQuery ToQuery(StorefrontSearchRequest request)
    {
        var errors = new List<ValidationError>();
        var sort = SearchSort.Relevance;
        if (!string.IsNullOrEmpty(request.Sort) && !Sorts.TryGetValue(request.Sort, out sort))
            errors.Add(new("sort", "sort must be relevance, price-asc, price-desc or most-sold."));

        var query = new SearchQuery
        {
            Text = request.Text?.Trim() ?? "",
            Page = request.Page,
            PageSize = request.PageSize,
            BrandSlugs = request.Brands.Where(b => !string.IsNullOrWhiteSpace(b)).Distinct(StringComparer.Ordinal).ToList(),
            CategorySlug = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category,
            InStockOnly = request.InStockOnly,
            MinPriceMinor = request.MinPriceMinor,
            MaxPriceMinor = request.MaxPriceMinor,
            Sort = sort,
        };
        errors.AddRange(query.Validate().Select(problem => new ValidationError("query", problem)));
        if (errors.Count > 0) throw new ValidationException(errors);
        return query;
    }

    private static StorefrontSearchResult Map(SearchResultDto<ProductSearchDocument> result, string matchedBy) => new(
        result.Hits.Select(d => new StorefrontSearchHit(
            d.Id, d.Slug, d.TitleEn, d.TitleBn, d.BrandName, d.BrandSlug, d.PartNumbers,
            d.PriceMinor, d.Currency, d.InStock, d.SupplyStatus, d.RatingAvg)).ToList(),
        result.Total, result.Page, result.PageSize, matchedBy, result.ProcessingMs, result.Facets);
}
