using System.Globalization;
using System.Text;
using Mechware.Application.Catalog;
using Mechware.Domain.Cart;
using Mechware.Domain.Catalog;
using Mechware.Domain.Common;
using Mechware.Domain.Common.Exceptions;

namespace Mechware.Infrastructure.Catalog;

/// <summary>
/// Bulk import, dry run (C10.1): reads the CSV as a stream, checks every row against the catalogue rules and reports.
/// Writes nothing; its only database access is the read-only <see cref="ICatalogImportLookup"/>. Format: docs/catalog/IMPORT_FORMAT.md.
/// </summary>
public sealed class CatalogImportValidator(ICatalogImportLookup lookup, CatalogImportOptions options) : ICatalogImportValidator
{
    public const string AttributePrefix = "attr:";
    public const string FormulaStarts = "=+-@\t\r";

    private static readonly string[] Required =
        ["product_slug", "product_title_en", "product_title_bn", "category", "sku", "manufacturer_part_number", "price_bdt"];
    private static readonly string[] Optional = ["brand", "min_order_qty", "pack_size", "supply_status", "datasheet_url", "image_url"];
    private static readonly string[] Numeric = ["price_bdt", "min_order_qty", "pack_size"];

    private sealed class Kept(int row, string slug, string? key, string sku)
    {
        public int Row { get; } = row;
        public string Slug { get; } = slug;
        public string? Key { get; } = key;
        public string Sku { get; } = sku;
        public bool ExistingVariant { get; set; }
    }

    private sealed class Run(int maxErrors)
    {
        public List<CatalogImportRowError> Errors { get; } = [];
        public HashSet<int> Invalid { get; } = [];
        public int ErrorCount { get; private set; }

        public void Add(int row, string column, string code, string message)
        {
            ErrorCount++;
            if (row > 0) Invalid.Add(row);
            if (Errors.Count < maxErrors) Errors.Add(new CatalogImportRowError(row, column, code, message));
        }
    }

    public async Task<CatalogImportDryRun> DryRunAsync(Stream csv, CancellationToken ct = default) =>
        (await AnalyseAsync(csv, ct)).Report;

    public async Task<CatalogImportAnalysis> AnalyseAsync(Stream csv, CancellationToken ct = default)
    {
        var run = new Run(options.MaxErrors);
        var kept = new List<Kept>();
        var parsed = new List<CatalogImportRow>();
        var productValues = new Dictionary<(string Slug, string Column), (string Value, int Row)>();
        var existingSlugs = new HashSet<string>(StringComparer.Ordinal);
        int rows = 0;
        try
        {
            using var reader = new CatalogImportCsvReader(csv, options.MaxBytes, options.MaxFieldChars);
            var header = reader.Read(ct);
            if (header is null)
            {
                run.Add(0, "", "EMPTY_FILE", "The file has no header row.");
                return Result();
            }

            var attributes = (await lookup.AttributesAsync(ct)).ToDictionary(a => a.Code, StringComparer.Ordinal);
            var columns = ReadHeader(header, attributes, run);
            if (columns is null) return Result();

            var brandKeys = new HashSet<string>((await lookup.BrandsAsync(ct)).SelectMany(b => new[] { b.Slug, b.Name }), StringComparer.OrdinalIgnoreCase);
            var categoryKeys = new HashSet<string>((await lookup.CategoriesAsync(ct)).SelectMany(c => new[] { c.Slug, c.Path }), StringComparer.OrdinalIgnoreCase);
            var products = new Dictionary<string, (string, string, string, string)>(StringComparer.Ordinal);
            var partKeys = new Dictionary<string, (int Row, string Raw)>(StringComparer.Ordinal);
            var skus = new Dictionary<string, int>(StringComparer.Ordinal);

            string[]? record;
            while ((record = reader.Read(ct)) is not null)
            {
                int row = ++rows + 1;
                if (rows > options.MaxRows) throw new CatalogImportFileException("TOO_MANY_ROWS", $"The file has more than {options.MaxRows} rows.");
                if (record.Length != header.Length)
                {
                    run.Add(row, "", "COLUMN_COUNT", $"The row has {record.Length} fields; the header has {header.Length}.");
                    continue;
                }

                var cells = columns.ToDictionary(c => c.Key, c => Clean(record[c.Value], c.Key, row, run));
                string slug = cells["product_slug"], titleEn = cells["product_title_en"], titleBn = cells["product_title_bn"];
                string brand = cells.GetValueOrDefault("brand", ""), category = cells["category"];
                string sku = cells["sku"].ToUpperInvariant(), pn = cells["manufacturer_part_number"];

                foreach (var name in Required)
                    if (cells[name].Length == 0) run.Add(row, name, "REQUIRED", $"{name} is required.");
                if (slug.Length > 0 && !Slug.IsValid(slug))
                    run.Add(row, "product_slug", "INVALID_SLUG", "The slug must be lower-case letters, digits and single hyphens (at most 255 characters).");
                if (titleEn.Length > 255) run.Add(row, "product_title_en", "TOO_LONG", "The title is at most 255 characters.");
                if (titleBn.Length > 255) run.Add(row, "product_title_bn", "TOO_LONG", "The title is at most 255 characters.");
                if (brand.Length > 0 && !brandKeys.Contains(brand)) run.Add(row, "brand", "UNKNOWN_BRAND", $"No brand '{brand}' (use its slug or name).");
                if (category.Length > 0 && !categoryKeys.Contains(category)) run.Add(row, "category", "UNKNOWN_CATEGORY", $"No category '{category}' (use its slug or path).");
                if (sku.Length > 100 || sku.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
                    run.Add(row, "sku", "INVALID_SKU", "A SKU is at most 100 characters, without spaces or control characters.");

                string? key = CheckPartNumber(pn, row, partKeys, run);
                if (sku.Length > 0 && !skus.TryAdd(sku, row)) run.Add(row, "sku", "DUPLICATE_SKU", $"The SKU is already in row {skus[sku]}.");

                if (slug.Length > 0)
                {
                    var identity = (titleEn, titleBn, brand.ToLowerInvariant(), category.ToLowerInvariant());
                    if (!products.TryAdd(slug, identity) && products[slug] != identity)
                        run.Add(row, "product_slug", "PRODUCT_MISMATCH", "Rows of one product must agree on titles, brand and category.");
                }

                CheckPrice(cells["price_bdt"], row, run);
                CheckOrderUnits(cells.GetValueOrDefault("min_order_qty", ""), cells.GetValueOrDefault("pack_size", ""), row, run);
                string supply = cells.GetValueOrDefault("supply_status", "");
                if (supply.Length > 0 && !(supply.All(char.IsLetter) && Enum.TryParse<SupplyStatus>(supply, true, out _)))
                    run.Add(row, "supply_status", "INVALID_SUPPLY_STATUS", "Use active, nrnd or obsolete.");
                foreach (var url in new[] { "datasheet_url", "image_url" })
                    if (cells.GetValueOrDefault(url, "") is { Length: > 0 } u && !(u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && SafeUrl.IsSafe(u)))
                        run.Add(row, url, "INVALID_URL", "The URL must be https://, without spaces or special characters.");
                foreach (var name in columns.Keys.Where(n => n.StartsWith(AttributePrefix, StringComparison.Ordinal)))
                    if (cells[name].Length > 0) CheckAttribute(attributes[name[AttributePrefix.Length..]], name, cells[name], row, run);

                if (slug.Length > 0)
                {
                    CheckProductValues(slug, cells, row, productValues, run);
                    kept.Add(new Kept(row, slug, key, sku));
                }

                if (!run.Invalid.Contains(row)) parsed.Add(ToRow(row, cells, sku, key!));
            }

            await CheckExistingAsync(kept, existingSlugs, run, ct);
        }
        catch (CatalogImportFileException ex)
        {
            run.Add(0, "", ex.Code, ex.Message);
        }

        return Result();

        CatalogImportAnalysis Result() =>
            new(Report(), run.ErrorCount > 0 ? [] : [.. parsed.Where(r => !run.Invalid.Contains(r.Row))]);

        CatalogImportDryRun Report()
        {
            var good = kept.Where(k => !run.Invalid.Contains(k.Row)).ToList();
            int products = good.Select(k => k.Slug).Distinct().Count();
            int existingProducts = good.Where(k => existingSlugs.Contains(k.Slug)).Select(k => k.Slug).Distinct().Count();
            int existingVariants = good.Count(k => k.ExistingVariant);
            int invalid = run.Invalid.Count;
            return new CatalogImportDryRun(rows, rows - invalid, invalid, products - existingProducts, existingProducts,
                good.Count - existingVariants, existingVariants, run.ErrorCount, run.ErrorCount > run.Errors.Count,
                [.. run.Errors.OrderBy(e => e.Row)]);
        }
    }

    private static CatalogImportRow ToRow(int row, Dictionary<string, string> cells, string sku, string key)
    {
        string Cell(string name) => cells.GetValueOrDefault(name, "");
        decimal price = decimal.Parse(cells["price_bdt"], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        return new CatalogImportRow(
            row, cells["product_slug"], cells["product_title_en"], cells["product_title_bn"],
            Cell("brand") is { Length: > 0 } b ? b : null, cells["category"], sku, cells["manufacturer_part_number"], key,
            (long)(price * 100m),
            Cell("min_order_qty") is { Length: > 0 } m ? int.Parse(m, CultureInfo.InvariantCulture) : null,
            Cell("pack_size") is { Length: > 0 } p ? int.Parse(p, CultureInfo.InvariantCulture) : null,
            Cell("supply_status") is { Length: > 0 } s ? Enum.Parse<SupplyStatus>(s, true) : null,
            Cell("datasheet_url") is { Length: > 0 } d ? d : null,
            cells.Where(c => c.Key.StartsWith(AttributePrefix, StringComparison.Ordinal) && c.Value.Length > 0)
                .ToDictionary(c => c.Key[AttributePrefix.Length..], c => c.Value, StringComparer.Ordinal));
    }

    /// <summary>Supply status, datasheet and attributes belong to the product: rows of one product may leave them blank but not disagree.</summary>
    private static void CheckProductValues(string slug, Dictionary<string, string> cells, int row,
        Dictionary<(string Slug, string Column), (string Value, int Row)> seen, Run run)
    {
        foreach (var (column, value) in cells)
        {
            if (value.Length == 0 || !(column is "supply_status" or "datasheet_url" || column.StartsWith(AttributePrefix, StringComparison.Ordinal))) continue;
            if (!seen.TryAdd((slug, column), (value, row)) && !string.Equals(seen[(slug, column)].Value, value, StringComparison.OrdinalIgnoreCase))
                run.Add(row, column, "PRODUCT_VALUE_CONFLICT", $"Row {seen[(slug, column)].Row} gives this product a different {column}.");
        }
    }

    private static Dictionary<string, int>? ReadHeader(string[] header, Dictionary<string, ImportAttributeRef> attributes, Run run)
    {
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < header.Length; i++)
        {
            string name = header[i].Trim().ToLowerInvariant();
            if (columns.ContainsKey(name)) run.Add(0, name, "DUPLICATE_COLUMN", $"The column '{name}' appears twice.");
            else if (name.StartsWith(AttributePrefix, StringComparison.Ordinal))
            {
                if (!attributes.ContainsKey(name[AttributePrefix.Length..])) run.Add(0, name, "UNKNOWN_ATTRIBUTE", $"No attribute with the code '{name[AttributePrefix.Length..]}'.");
                else columns[name] = i;
            }
            else if (Required.Contains(name) || Optional.Contains(name)) columns[name] = i;
            else run.Add(0, name, "UNKNOWN_COLUMN", $"The column '{name}' is not part of the import format.");
        }

        foreach (var name in Required.Where(r => !columns.ContainsKey(r))) run.Add(0, name, "MISSING_COLUMN", $"The required column '{name}' is missing.");
        return run.ErrorCount == 0 ? columns : null;
    }

    /// <summary>NFC-normalises and trims; flags a value that starts like a spreadsheet formula (text columns only).</summary>
    private static string Clean(string raw, string column, int row, Run run)
    {
        if (raw.Length > 0 && FormulaStarts.Contains(raw[0]) && !Numeric.Contains(column))
            run.Add(row, column, "FORMULA_INJECTION", "A value may not start with = + - @ or a tab or carriage return (spreadsheet formula risk).");
        try
        {
            return raw.Trim().Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            run.Add(row, column, "INVALID_TEXT", "The text contains invalid Unicode.");
            return "";
        }
    }

    private static string? CheckPartNumber(string pn, int row, Dictionary<string, (int Row, string Raw)> seen, Run run)
    {
        if (pn.Length == 0) return null;
        string key = PartNumbers.Normalise(pn);
        if (pn.Length > PartNumbers.MaxLength)
            run.Add(row, "manufacturer_part_number", "TOO_LONG", $"A part number is at most {PartNumbers.MaxLength} characters.");
        else if (pn.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format) || key.Length < 2)
            run.Add(row, "manufacturer_part_number", "INVALID_PART_NUMBER", "A part number needs at least two letters or digits and no control or invisible characters.");
        else if (seen.TryGetValue(key, out var first))
        {
            bool same = string.Equals(first.Raw, pn, StringComparison.OrdinalIgnoreCase);
            run.Add(row, "manufacturer_part_number", same ? "DUPLICATE_PART_NUMBER" : "NORMALISED_COLLISION",
                same ? $"The part number is already in row {first.Row}."
                     : $"'{pn}' is the same part number as '{first.Raw}' in row {first.Row} once spaces and punctuation are ignored.");
        }
        else seen[key] = (row, pn);
        return key.Length > 0 ? key : null;
    }

    private static void CheckPrice(string text, int row, Run run)
    {
        if (text.Length == 0) return;
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal price))
            run.Add(row, "price_bdt", "INVALID_PRICE", "The price is a plain number such as 1250 or 1250.50 (no currency sign, commas or signs).");
        else if (price <= 0) run.Add(row, "price_bdt", "PRICE_NOT_POSITIVE", "The price must be greater than zero.");
        else if (price != decimal.Round(price, 2)) run.Add(row, "price_bdt", "PRICE_PRECISION", "The price has at most 2 decimals.");
        else if (price > UnitCatalog.MaxValue) run.Add(row, "price_bdt", "PRICE_TOO_LARGE", "The price is out of range.");
    }

    private static void CheckOrderUnits(string moqText, string packText, int row, Run run)
    {
        int moq = 1, pack = 1;
        bool ok = true;
        if (moqText.Length > 0 && !(int.TryParse(moqText, NumberStyles.None, CultureInfo.InvariantCulture, out moq) && moq >= 1 && moq <= CartEngine.MaxQuantityPerLine))
        {
            run.Add(row, "min_order_qty", "INVALID_MOQ", $"The minimum order quantity is a whole number from 1 to {CartEngine.MaxQuantityPerLine}.");
            ok = false;
        }

        if (packText.Length > 0 && !(int.TryParse(packText, NumberStyles.None, CultureInfo.InvariantCulture, out pack) && pack >= 1))
        {
            run.Add(row, "pack_size", "INVALID_PACK_SIZE", "The pack size is a whole number of at least 1.");
            ok = false;
        }

        if (ok && moq % pack != 0) run.Add(row, "min_order_qty", "MOQ_NOT_PACK_MULTIPLE", "The minimum order quantity must be a whole number of packs.");
    }

    private static void CheckAttribute(ImportAttributeRef def, string column, string value, int row, Run run)
    {
        if (def.DataType is AttributeDataType.Number or AttributeDataType.Range)
        {
            try
            {
                UnitCatalog.Parse(value, def.Unit!, allowRange: def.DataType == AttributeDataType.Range);   // converted to the attribute's unit
            }
            catch (ValidationException ex)
            {
                run.Add(row, column, "INVALID_UNIT_VALUE", ex.Errors[0].Message);
            }
        }
        else if (def.DataType == AttributeDataType.Boolean)
        {
            if (!new[] { "true", "false", "yes", "no", "1", "0" }.Contains(value.ToLowerInvariant()))
                run.Add(row, column, "INVALID_BOOLEAN", "Use true or false.");
        }
        else if (value.Length > UnitCatalog.MaxTextLength)
            run.Add(row, column, "TOO_LONG", $"A value is at most {UnitCatalog.MaxTextLength} characters.");
    }

    /// <summary>Existing products and variants, matched by slug, part number key and SKU. Rows that clash with another product's part are errors.</summary>
    private async Task CheckExistingAsync(List<Kept> kept, HashSet<string> existingSlugs, Run run, CancellationToken ct)
    {
        foreach (var chunk in kept.Select(k => k.Slug).Distinct().Chunk(1000))
            existingSlugs.UnionWith(await lookup.ExistingProductSlugsAsync(chunk, ct));

        var variants = new List<ImportVariantRef>();
        foreach (var chunk in kept.Chunk(1000))
            variants.AddRange(await lookup.ExistingVariantsAsync(
                [.. chunk.Where(k => k.Key is not null).Select(k => k.Key!)], [.. chunk.Select(k => k.Sku)], ct));
        var byKey = variants.Where(v => v.PartNumberKey is not null).GroupBy(v => v.PartNumberKey!).ToDictionary(g => g.Key, g => g.First());
        var bySku = variants.GroupBy(v => v.Sku).ToDictionary(g => g.Key, g => g.First());

        foreach (var k in kept)
        {
            if (run.Invalid.Contains(k.Row)) continue;
            ImportVariantRef? byPart = k.Key is not null && byKey.TryGetValue(k.Key, out var p) ? p : null;
            ImportVariantRef? bySkuHit = bySku.GetValueOrDefault(k.Sku);
            if (byPart is not null && byPart.ProductSlug != k.Slug)
                run.Add(k.Row, "manufacturer_part_number", "PART_NUMBER_IN_OTHER_PRODUCT", $"The part number already belongs to product '{byPart.ProductSlug}'.");
            else if (bySkuHit is not null && (bySkuHit.ProductSlug != k.Slug || (byPart is not null && byPart.Sku != bySkuHit.Sku)))
                run.Add(k.Row, "sku", "SKU_IN_USE", "The SKU already belongs to a different part.");
            else k.ExistingVariant = byPart is not null || bySkuHit is not null;
        }
    }
}
