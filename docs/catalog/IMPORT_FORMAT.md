# Catalogue import file format (C10)

Status: C10.1 (dry run) and C10.2 (commit) done. C10.4 will download the image URLs.

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
| `supply_status`, `datasheet_url`, `attr:<code>` (product level) | | Belong to the product, not the SKU: rows of one product may leave them blank, but two different values are `PRODUCT_VALUE_CONFLICT`. |
| `attr:<code>` | no | One column per attribute definition code, e.g. `attr:rated_current`. Number and range attributes take a value with a unit (`10 A`, `6 kA`, `230 V`, `0-10 bar`), read by `UnitCatalog.Parse` and converted to the attribute's own unit; wrong quantity, range on a single-value attribute, or absurd values are errors. Text attributes: at most 150 characters. Boolean: true/false/yes/no/1/0. An unknown code is a header error. |

## Safety
A text value starting with `=`, `+`, `-`, `@`, tab or carriage return is refused (`FORMULA_INJECTION`): a spreadsheet would run it as a formula. Numeric columns are checked by their own rules. The export side must apply the same guard.

## Report (`POST /api/v1/admin/catalog/import/dry-run`, multipart field `file`, permission `catalog.manage`)
`rows`, `validRows`, `invalidRows`, `newProducts`, `existingProducts`, `newVariants`, `existingVariants` (valid rows only; a variant exists when its part-number key or SKU is in the catalogue), `errorCount`, `errorsTruncated`, `errors[]` of `{row, column, code, message}`. Nothing is written.

## Commit (`POST /api/v1/admin/catalog/import/commit`, multipart fields `file` and `sellerId`, permission `catalog.manage`)
- Runs the same checks first. Any error (file, row, or a domain rule hit while writing) means **nothing is written**: `422`, code `IMPORT_REJECTED`, and `data` is the dry-run report (`DOMAIN_RULE` rows are added for rules found while writing, e.g. a minimum quantity that is not a whole number of the stored pack size).
- `sellerId` is required: an approved seller. New products belong to it (status `draft`; publishing stays the normal review path); an existing product of another seller is an error. Brands and categories must already exist; the import never creates them.
- Matching: product by `product_slug`; variant by part-number key or SKU (the dry-run rules already refuse a part or SKU that belongs to a different product). New variants get the option `Model = <manufacturer_part_number>`, weight 0.5 kg, no stock.
- Idempotent: a value is written only when it differs from the stored one, and a blank optional cell leaves the stored value alone, so the same file again reports `updated* = 0` and writes no row and no event. Titles, category, brand (if given), datasheet, supply status (an existing successor is kept while the product stays phased out), part number text, price, order units and attribute values are updated. `image_url` is ignored until C10.4. A changed attribute value replaces the product's old value of that attribute; a number matches an existing value by its converted number ("6 kA" finds "6000 A").
- One transaction, behind a database-wide advisory lock (`catalog-import`), so overlapping imports run one after the other and the second sees the first's rows; SaveChanges runs in batches of 500 products inside it. The unique SKU index is the second line of defence (409 `CONCURRENT_MODIFICATION`, nothing written).
- Search and cache: products are changed through the domain methods, which raise `ProductUpdatedEvent` (outbox, then Meilisearch queue and cache eviction); a price change calls `RequestReindex`.
- Audit: the entity audit rows, plus one summary row `catalog.import.committed` (entity `catalog_import`, id = file SHA-256, new values = the totals; actor and IP from the request).
- Response `data`: `sha256`, `rows`, `createdProducts`, `createdVariants`, `updatedProducts` (an existing product where anything changed, its variants included), `updatedVariants`, `unchangedProducts`, `unchangedVariants`.
- No step-up code: step-up guards credit, refunds and authenticator set-up; no other catalogue edit asks for it and the import only creates drafts.

## Error codes
File/header (row 0): `EMPTY_FILE`, `INVALID_ENCODING`, `MALFORMED_CSV`, `FILE_TOO_LARGE`, `TOO_MANY_ROWS`, `FIELD_TOO_LONG`, `MISSING_COLUMN`, `UNKNOWN_COLUMN`, `DUPLICATE_COLUMN`, `UNKNOWN_ATTRIBUTE`.
Row: `COLUMN_COUNT`, `REQUIRED`, `INVALID_SLUG`, `TOO_LONG`, `UNKNOWN_BRAND`, `UNKNOWN_CATEGORY`, `INVALID_SKU`, `INVALID_PART_NUMBER`, `DUPLICATE_PART_NUMBER`, `NORMALISED_COLLISION`, `DUPLICATE_SKU`, `PRODUCT_MISMATCH`, `INVALID_PRICE`, `PRICE_NOT_POSITIVE`, `PRICE_PRECISION`, `PRICE_TOO_LARGE`, `INVALID_MOQ`, `INVALID_PACK_SIZE`, `MOQ_NOT_PACK_MULTIPLE`, `INVALID_SUPPLY_STATUS`, `INVALID_URL`, `INVALID_UNIT_VALUE`, `INVALID_BOOLEAN`, `FORMULA_INJECTION`, `INVALID_TEXT`, `PART_NUMBER_IN_OTHER_PRODUCT`, `SKU_IN_USE`, `PRODUCT_VALUE_CONFLICT`. Commit only: `IMPORT_REJECTED` (422 envelope), `DOMAIN_RULE`.
