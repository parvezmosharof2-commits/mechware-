using System.Globalization;
using Mechware.Application.Search;

namespace Mechware.Infrastructure.Search;

/// <summary>
/// Turns a validated <see cref="SearchQuery"/> into a Meilisearch filter expression. Values are quoted, and anything outside
/// the slug alphabet is refused instead of escaped, so a request can never add a clause of its own.
/// </summary>
internal static class SearchFilterBuilder
{
    public static string? Build(SearchQuery query)
    {
        var problems = query.Validate();
        if (problems.Count > 0)
            throw new ArgumentException("Invalid search query: " + string.Join("; ", problems), nameof(query));

        var clauses = new List<string>();
        if (query.PartNumberKey is not null)
            clauses.Add($"partNumberKeys = \"{query.PartNumberKey}\"");
        if (query.BrandSlugs.Count > 0)
            clauses.Add("(" + string.Join(" OR ", query.BrandSlugs.Distinct().Select(b => $"brandSlug = \"{b}\"")) + ")");
        if (query.CategorySlug is not null)
            clauses.Add($"categoryPath = \"{query.CategorySlug}\"");
        if (query.InStockOnly)
            clauses.Add("inStock = true");
        if (query.MinPriceMinor is { } min)
            clauses.Add($"priceMinor >= {min.ToString(CultureInfo.InvariantCulture)}");
        if (query.MaxPriceMinor is { } max)
            clauses.Add($"priceMinor <= {max.ToString(CultureInfo.InvariantCulture)}");

        if (query.MeasureQuantity is { } quantity)
        {
            if (query.MeasureMin is { } mMin) clauses.Add($"measures.{quantity} >= {Num(mMin)}");
            if (query.MeasureMax is { } mMax) clauses.Add($"measures.{quantity} <= {Num(mMax)}");
        }
        foreach (var range in query.AttributeRanges)
        {
            if (range.Min is { } rMin) clauses.Add($"numericAttributes.{range.Code} >= {Num(rMin)}");
            if (range.Max is { } rMax) clauses.Add($"numericAttributes.{range.Code} <= {Num(rMax)}");
        }

        return clauses.Count == 0 ? null : string.Join(" AND ", clauses);
    }

    // Through decimal: never exponent notation ("1E-05"), which the filter syntax does not read. Inputs are bounded (≤ 24 chars, no exponent).
    internal static string Num(double value) => ((decimal)value).ToString(CultureInfo.InvariantCulture);

    public static string[]? BuildSort(SearchSort sort) => sort switch
    {
        SearchSort.PriceAscending => ["priceMinor:asc"],
        SearchSort.PriceDescending => ["priceMinor:desc"],
        SearchSort.MostSold => ["soldCount:desc"],
        _ => null,
    };
}
