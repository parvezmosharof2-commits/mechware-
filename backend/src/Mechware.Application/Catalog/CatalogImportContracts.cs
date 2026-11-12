using Mechware.Domain.Catalog;

namespace Mechware.Application.Catalog;

/// <summary>Limits of one import file, so a huge or hostile file cannot exhaust memory. Defaults: 10 MB, 20,000 rows, 500 listed errors.</summary>
public sealed record CatalogImportOptions(long MaxBytes = 10L * 1024 * 1024, int MaxRows = 20_000, int MaxErrors = 500, int MaxFieldChars = 20_000);

/// <summary>One problem in the file. <c>Row</c> counts records with the header as row 1 (as a spreadsheet shows them); 0 means the whole file.</summary>
public sealed record CatalogImportRowError(int Row, string Column, string Code, string Message);

/// <summary>
/// What a dry run found. Nothing is written. <c>ErrorCount</c> is exact; <c>Errors</c> lists the first
/// <see cref="CatalogImportOptions.MaxErrors"/> by row. New / existing counts cover valid rows only (variants match by part number key or SKU).
/// </summary>
public sealed record CatalogImportDryRun(
    int Rows, int ValidRows, int InvalidRows,
    int NewProducts, int ExistingProducts, int NewVariants, int ExistingVariants,
    int ErrorCount, bool ErrorsTruncated, IReadOnlyList<CatalogImportRowError> Errors);

public interface ICatalogImportValidator
{
    /// <summary>Reads the CSV as a stream and reports. Never writes to the database.</summary>
    Task<CatalogImportDryRun> DryRunAsync(Stream csv, CancellationToken ct = default);
}

public sealed record ImportBrandRef(string Name, string Slug);

public sealed record ImportCategoryRef(string Slug, string Path);

public sealed record ImportAttributeRef(string Code, AttributeDataType DataType, string? Unit);

public sealed record ImportVariantRef(string Sku, string? PartNumberKey, string ProductSlug);

/// <summary>Read-only look-ups the dry run needs from the catalogue.</summary>
public interface ICatalogImportLookup
{
    Task<IReadOnlyList<ImportBrandRef>> BrandsAsync(CancellationToken ct);
    Task<IReadOnlyList<ImportCategoryRef>> CategoriesAsync(CancellationToken ct);
    Task<IReadOnlyList<ImportAttributeRef>> AttributesAsync(CancellationToken ct);
    Task<IReadOnlySet<string>> ExistingProductSlugsAsync(IReadOnlyCollection<string> slugs, CancellationToken ct);
    Task<IReadOnlyList<ImportVariantRef>> ExistingVariantsAsync(IReadOnlyCollection<string> partKeys, IReadOnlyCollection<string> skus, CancellationToken ct);
}
