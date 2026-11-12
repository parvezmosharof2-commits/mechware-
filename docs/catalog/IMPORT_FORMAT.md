# Catalogue import file format (C10)

Status: C10.1 (dry run, checks only). C10.2 will commit the same file; C10.4 downloads the URLs.

## File
- **CSV**, UTF-8 (a byte-order mark is accepted), RFC 4180: comma separator, `"` quotes, `""` for a quote, line breaks allowed inside quotes. Invalid UTF-8 is refused.
- **XLSX is not supported in C10.1.** If added later it must use a permissively licensed reader (ExcelDataReader, MIT, or ClosedXML, MIT) behind the same row model; EPPlus 5+ is commercial and must not be used. Export from Excel with "CSV UTF-8".
- Limits (options, `CatalogImportOptions`): 10 MB, 20,000 data rows, 20,000 characters per field; the first 500 errors are listed (the count is exact).
- One row per **variant** (one purchasable SKU). Rows of the same product repeat the product columns and must agree on them.
- Row numbers in the report count the header as row 1, as a spreadsheet shows them. Row 0 means the whole file or the header.

## Columns
Header names are lower-case; order is free; an unknown column is an error.

| Column | Required | Rule |
|---|---|---|
| `product_slug` | yes | Lower-case letters, digits, single hyphens, at most 255. Identifies the product (new if the slug is unused). |
| `product_title_en` / `product_title_bn` | yes | At most 255 characters each. Bangla accepted; text is Unicode NFC-normalised. |
| `brand` | no | Slug or name of an existing brand (case-insensitive). Empty = no brand. |
| `category` | yes | Slug or materialised path (`root.industrial-automation.plcs`) of an existing category. |
| `sku` | yes | MECHWARE SKU; at most 100, no spaces or control characters; stored upper-case; unique in the file and in the catalogue. |
| `manufacturer_part_number` | yes | As printed; at most 64 characters, no control or invisible characters, at least 2 letters or digits after normalisation. The match key is `PartNumbers.Normalise` (A-Z/0-9, upper case, Bangla digits count): `6ES7 214-1AG40` and `6es7214.1ag40` are the same part. |
| `price_bdt` | yes | Plain decimal, greater than 0, at most 2 decimals, no sign, comma or currency symbol. This is the value stored as the variant price (minor units = price x 100). The catalogue does not store a VAT basis per price: VAT is applied at order time from the category rate and the order's tax mode (`TaxMode`), so the import adds no VAT rule. |
| `min_order_qty` | no (1) | Whole number 1..10,000; must be a multiple of `pack_size`. |
| `pack_size` | no (1) | Whole number >= 1. |
| `supply_status` | no (`active`) | `active`, `nrnd` or `obsolete`. |
| `datasheet_url`, `image_url` | no | `https://` URLs only (checked here; downloaded in C10.4). |
| `attr:<code>` | no | One column per attribute definition code, e.g. `attr:rated_current`. Number and range attributes take a value with a unit (`10 A`, `6 kA`, `230 V`, `0-10 bar`), read by `UnitCatalog.Parse` and converted to the attribute's own unit; wrong quantity, range on a single-value attribute, or absurd values are errors. Text attributes: at most 150 characters. Boolean: true/false/yes/no/1/0. An unknown code is a header error. |

## Safety
A text value starting with `=`, `+`, `-`, `@`, tab or carriage return is refused (`FORMULA_INJECTION`): a spreadsheet would run it as a formula. Numeric columns are checked by their own rules. The export side must apply the same guard.

## Report (`POST /api/v1/admin/catalog/import/dry-run`, multipart field `file`, permission `catalog.manage`)
`rows`, `validRows`, `invalidRows`, `newProducts`, `existingProducts`, `newVariants`, `existingVariants` (valid rows only; a variant exists when its part-number key or SKU is in the catalogue), `errorCount`, `errorsTruncated`, `errors[]` of `{row, column, code, message}`. Nothing is written.

## Error codes
File/header (row 0): `EMPTY_FILE`, `INVALID_ENCODING`, `MALFORMED_CSV`, `FILE_TOO_LARGE`, `TOO_MANY_ROWS`, `FIELD_TOO_LONG`, `MISSING_COLUMN`, `UNKNOWN_COLUMN`, `DUPLICATE_COLUMN`, `UNKNOWN_ATTRIBUTE`.
Row: `COLUMN_COUNT`, `REQUIRED`, `INVALID_SLUG`, `TOO_LONG`, `UNKNOWN_BRAND`, `UNKNOWN_CATEGORY`, `INVALID_SKU`, `INVALID_PART_NUMBER`, `DUPLICATE_PART_NUMBER`, `NORMALISED_COLLISION`, `DUPLICATE_SKU`, `PRODUCT_MISMATCH`, `INVALID_PRICE`, `PRICE_NOT_POSITIVE`, `PRICE_PRECISION`, `PRICE_TOO_LARGE`, `INVALID_MOQ`, `INVALID_PACK_SIZE`, `MOQ_NOT_PACK_MULTIPLE`, `INVALID_SUPPLY_STATUS`, `INVALID_URL`, `INVALID_UNIT_VALUE`, `INVALID_BOOLEAN`, `FORMULA_INJECTION`, `INVALID_TEXT`, `PART_NUMBER_IN_OTHER_PRODUCT`, `SKU_IN_USE`.
