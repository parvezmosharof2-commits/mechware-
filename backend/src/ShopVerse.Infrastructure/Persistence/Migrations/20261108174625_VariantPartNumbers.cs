using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopVerse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VariantPartNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "manufacturer_part_number",
                table: "product_variants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "part_number_fuzzy_key",
                table: "product_variants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "part_number_key",
                table: "product_variants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_variants_part_number_fuzzy_key",
                table: "product_variants",
                column: "part_number_fuzzy_key");

            migrationBuilder.CreateIndex(
                name: "ix_variants_part_number_key",
                table: "product_variants",
                column: "part_number_key");

            // Existing rows: the default SKU takes the part number from the product's specs ("General" → "Manufacturer part
            // no.", else "Model", as the seed does), skipping any with control or invisible characters, and the keys are made with the same rule as PartNumbers.Normalise / Fuzzy (A–Z and 0–9 only, upper case,
            // Bangla digits as digits; O→0, I and L→1). Rows whose key would be shorter than two characters stay empty.
            migrationBuilder.Sql("""
                UPDATE product_variants v
                   SET manufacturer_part_number = left(src.mpn, 64)
                  FROM (SELECT id, btrim(coalesce(nullif(btrim(specs->'General'->>'Manufacturer part no.'), ''), specs->'General'->>'Model')) AS mpn
                          FROM products WHERE jsonb_typeof(specs->'General') = 'object') src
                 WHERE src.id = v.product_id AND v.is_default AND v.manufacturer_part_number IS NULL
                   AND coalesce(src.mpn, '') <> ''
                   AND src.mpn !~ '[[:cntrl:]\u200B-\u200F\u2028-\u202E\u2060-\u2064\uFEFF]';
                UPDATE product_variants
                   SET part_number_key = left(upper(regexp_replace(translate(manufacturer_part_number, '০১২৩৪৫৬৭৮৯', '0123456789'), '[^A-Za-z0-9]', '', 'g')), 64)
                 WHERE manufacturer_part_number IS NOT NULL;
                UPDATE product_variants SET manufacturer_part_number = NULL, part_number_key = NULL
                 WHERE part_number_key IS NOT NULL AND length(part_number_key) < 2;
                UPDATE product_variants SET part_number_fuzzy_key = translate(part_number_key, 'OIL', '011')
                 WHERE part_number_key IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_variants_part_number_fuzzy_key",
                table: "product_variants");

            migrationBuilder.DropIndex(
                name: "ix_variants_part_number_key",
                table: "product_variants");

            migrationBuilder.DropColumn(
                name: "manufacturer_part_number",
                table: "product_variants");

            migrationBuilder.DropColumn(
                name: "part_number_fuzzy_key",
                table: "product_variants");

            migrationBuilder.DropColumn(
                name: "part_number_key",
                table: "product_variants");
        }
    }
}
