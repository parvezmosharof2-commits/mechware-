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

        return clauses.Count == 0 ? null : string.Join(" AND ", clauses);
    }

    public static string[]? BuildSort(SearchSort sort) => sort switch
    {
        SearchSort.PriceAscending => ["priceMinor:asc"],
        SearchSort.PriceDescending => ["priceMinor:desc"],
        SearchSort.MostSold => ["soldCount:desc"],
        _ => null,
    };
}
