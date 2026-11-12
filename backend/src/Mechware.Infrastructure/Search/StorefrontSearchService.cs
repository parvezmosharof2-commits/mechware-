using System.Globalization;
using Mechware.Application.Search;
using Mechware.Domain.Catalog;
using Mechware.Domain.Common.Exceptions;

namespace Mechware.Infrastructure.Search;

/// <inheritdoc cref="IStorefrontSearch"/>
public sealed class StorefrontSearchService(ISearchProvider search) : IStorefrontSearch
{
    public const string MatchedByPartNumber = "partNumber";
    public const string MatchedByMeasure = "measure";
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
            // The fuzzy form of the input matches the fuzzy key stored for every part number and SKU, so O/0 and I/L/1 mix-ups hit too.
            var key = PartNumbers.Fuzzy(PartNumbers.Normalise(query.Text));
            var byKey = await search.SearchAsync(query with { Text = "", PartNumberKey = key }, ct);
            if (byKey.Total > 0) return Map(byKey, MatchedByPartNumber);
        }

        // "750 W" finds a product whose power is 0.75 kW: the value is converted to the base unit and matched within 0.5 %.
        if (UnitCatalog.TryReadMeasure(query.Text, out var quantity, out var baseValue, out var remainder))
        {
            var value = (double)baseValue;
            var byMeasure = await search.SearchAsync(
                query with { Text = remainder, MeasureQuantity = quantity, MeasureMin = value * 0.995, MeasureMax = value * 1.005 }, ct);
            if (byMeasure.Total > 0) return Map(byMeasure, MatchedByMeasure);
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
            AttributeRanges = ParseAttributes(request.Attributes, errors),
        };
        errors.AddRange(query.Validate().Select(problem => new ValidationError("query", problem)));
        if (errors.Count > 0) throw new ValidationException(errors);
        return query;
    }

    /// <summary>Reads <c>code:min..max</c> filters; either bound may be empty. Bad syntax is reported, never guessed at.</summary>
    private static List<AttributeRange> ParseAttributes(IReadOnlyList<string> raw, List<ValidationError> errors)
    {
        var ranges = new List<AttributeRange>();
        foreach (var item in raw.Where(a => !string.IsNullOrWhiteSpace(a)))
        {
            var colon = item.IndexOf(':');
            var dots = colon < 0 ? -1 : item.IndexOf("..", colon + 1, StringComparison.Ordinal);
            if (colon < 0 || dots < 0 || !SearchAttributeCode.IsValid(item[..colon])
                || !TryBound(item[(colon + 1)..dots], out var min) || !TryBound(item[(dots + 2)..], out var max))
            {
                errors.Add(new("attr", "attr must look like code:min..max (either side may be empty), e.g. power_kw:0.5..1."));
                continue;
            }
            ranges.Add(new AttributeRange(item[..colon], min, max));
        }
        return ranges;
    }

    private static bool TryBound(string text, out double? value)
    {
        value = null;
        if (text.Length == 0) return true;
        if (text.Length > 24 || !double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
            || !double.IsFinite(parsed)) return false;
        value = parsed;
        return true;
    }

    private static StorefrontSearchResult Map(SearchResultDto<ProductSearchDocument> result, string matchedBy) => new(
        result.Hits.Select(d => new StorefrontSearchHit(
            d.Id, d.Slug, d.TitleEn, d.TitleBn, d.BrandName, d.BrandSlug, d.PartNumbers,
            d.PriceMinor, d.Currency, d.InStock, d.SupplyStatus, d.RatingAvg)).ToList(),
        result.Total, result.Page, result.PageSize, matchedBy, result.ProcessingMs, result.Facets);
}
