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

/// <summary>One valid file row, typed. Blank optional cells are null ("not in the file": an update leaves that field alone).</summary>
public sealed record CatalogImportRow(
    int Row, string Slug, string TitleEn, string TitleBn, string? Brand, string Category, string Sku, string PartNumber, string PartKey,
    long PriceMinor, int? MinOrderQty, int? PackSize, SupplyStatus? SupplyStatus, string? DatasheetUrl,
    IReadOnlyDictionary<string, string> Attributes);

/// <summary><c>Rows</c> is filled only when the file has no error at all.</summary>
public sealed record CatalogImportAnalysis(CatalogImportDryRun Report, IReadOnlyList<CatalogImportRow> Rows);

/// <summary>Totals of a committed file. "Updated" counts existing rows where at least one stored value changed.</summary>
public sealed record CatalogImportCommitResult(
    string Sha256, int Rows, int CreatedProducts, int CreatedVariants, int UpdatedProducts, int UpdatedVariants,
    int UnchangedProducts, int UnchangedVariants);

/// <summary>Either the totals, or (nothing written) the report that says why.</summary>
public sealed record CatalogImportCommitOutcome(CatalogImportCommitResult? Result, CatalogImportDryRun? Rejected);

public interface ICatalogImportCommitter
{
    /// <summary>All-or-nothing, in one transaction, serialised with other imports. Products are created as drafts for <paramref name="sellerId"/>.</summary>
    Task<CatalogImportCommitOutcome> CommitAsync(Stream csv, string sha256, Guid sellerId, CancellationToken ct = default);
}

public interface ICatalogImportValidator
{
    /// <summary>Like <see cref="DryRunAsync"/>, and also returns the typed rows when the file is clean.</summary>
    Task<CatalogImportAnalysis> AnalyseAsync(Stream csv, CancellationToken ct = default);

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
