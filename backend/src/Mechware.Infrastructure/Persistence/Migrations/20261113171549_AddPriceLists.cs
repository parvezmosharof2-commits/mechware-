using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mechware.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "margin_floors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: true),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    min_markup_percent = table.Column<decimal>(type: "numeric", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_margin_floors", x => x.id);
                    table.CheckConstraint("ck_margin_floors_markup", "min_markup_percent >= 0 AND min_markup_percent <= 1000 AND min_markup_percent = round(min_markup_percent, 2)");
                    table.CheckConstraint("ck_margin_floors_shape", "(scope = 'Global' AND brand_id IS NULL AND category_id IS NULL) OR (scope = 'Category' AND brand_id IS NULL AND category_id IS NOT NULL) OR (scope = 'Brand' AND brand_id IS NOT NULL AND category_id IS NULL) OR (scope = 'BrandAndCategory' AND brand_id IS NOT NULL AND category_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_margin_floors_brands_brand_id",
                        column: x => x.brand_id,
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_margin_floors_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "price_lists",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    series_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    contract_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    valid_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    valid_to = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    submitted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    effective_from_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    rejected_by = table.Column<Guid>(type: "uuid", nullable: true),
                    rejected_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    rejection_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    withdrawn_by = table.Column<Guid>(type: "uuid", nullable: true),
                    withdrawn_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    withdraw_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_price_lists", x => x.id);
                    table.CheckConstraint("ck_price_lists_approval_shape", "(status IN ('Approved', 'Withdrawn')) = (approved_by IS NOT NULL) AND (approved_by IS NULL) = (approved_at IS NULL) AND (approved_by IS NULL) = (effective_from_utc IS NULL)");
                    table.CheckConstraint("ck_price_lists_company_shape", "(scope = 'Company' AND company_id IS NOT NULL AND contract_ref IS NOT NULL) OR (scope <> 'Company' AND company_id IS NULL AND contract_ref IS NULL)");
                    table.CheckConstraint("ck_price_lists_content_hash", "content_hash IS NULL OR content_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_price_lists_currency", "currency = 'BDT'");
                    table.CheckConstraint("ck_price_lists_effective_from", "effective_from_utc IS NULL OR (effective_from_utc >= valid_from AND effective_from_utc >= approved_at + interval '60 seconds' AND effective_from_utc < valid_to)");
                    table.CheckConstraint("ck_price_lists_four_eyes", "approved_by IS NULL OR (submitted_by IS NOT NULL AND approved_by <> created_by AND approved_by <> submitted_by)");
                    table.CheckConstraint("ck_price_lists_rejection_shape", "(rejected_by IS NULL) = (rejected_at IS NULL) AND (rejected_by IS NULL) = (rejection_reason IS NULL)");
                    table.CheckConstraint("ck_price_lists_scope", "scope IN ('General', 'Group', 'Company')");
                    table.CheckConstraint("ck_price_lists_status", "status IN ('Draft', 'PendingApproval', 'Approved', 'Withdrawn')");
                    table.CheckConstraint("ck_price_lists_status_shape", "(status = 'Draft' AND submitted_by IS NULL AND submitted_at IS NULL AND content_hash IS NULL) OR (status <> 'Draft' AND submitted_by IS NOT NULL AND submitted_at IS NOT NULL AND content_hash IS NOT NULL)");
                    table.CheckConstraint("ck_price_lists_version", "version >= 1");
                    table.CheckConstraint("ck_price_lists_window", "valid_to > valid_from AND valid_to - valid_from <= interval '366 days'");
                    table.CheckConstraint("ck_price_lists_withdrawal_shape", "(status = 'Withdrawn') = (withdrawn_by IS NOT NULL) AND (withdrawn_by IS NULL) = (withdrawn_at IS NULL) AND (withdrawn_by IS NULL) = (withdraw_reason IS NULL)");
                    table.ForeignKey(
                        name: "FK_price_lists_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "price_list_contributors",
                columns: table => new
                {
                    price_list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_contributed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_price_list_contributors", x => new { x.price_list_id, x.user_id });
                    table.ForeignKey(
                        name: "FK_price_list_contributors_price_lists_price_list_id",
                        column: x => x.price_list_id,
                        principalTable: "price_lists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "price_list_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    price_list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    min_qty = table.Column<int>(type: "integer", nullable: false),
                    unit_price_minor = table.Column<long>(type: "bigint", nullable: false),
                    below_floor_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    cost_minor_at_approval = table.Column<long>(type: "bigint", nullable: true),
                    floor_minor_at_approval = table.Column<long>(type: "bigint", nullable: true),
                    below_floor_at_approval = table.Column<bool>(type: "boolean", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_price_list_entries", x => x.id);
                    table.CheckConstraint("ck_price_list_entries_approval_values", "(cost_minor_at_approval IS NULL OR cost_minor_at_approval > 0) AND (floor_minor_at_approval IS NULL OR floor_minor_at_approval > 0)");
                    table.CheckConstraint("ck_price_list_entries_min_qty", "min_qty >= 1 AND min_qty <= 10000");
                    table.CheckConstraint("ck_price_list_entries_price", "unit_price_minor > 0 AND unit_price_minor <= 100000000000");
                    table.CheckConstraint("ck_price_list_entries_reason", "below_floor_reason IS NULL OR char_length(below_floor_reason) BETWEEN 5 AND 500");
                    table.ForeignKey(
                        name: "FK_price_list_entries_price_lists_price_list_id",
                        column: x => x.price_list_id,
                        principalTable: "price_lists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_price_list_entries_product_variants_variant_id",
                        column: x => x.variant_id,
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_margin_floors_brand_id",
                table: "margin_floors",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "IX_margin_floors_category_id",
                table: "margin_floors",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ux_margin_floors_scope_brand_category",
                table: "margin_floors",
                columns: new[] { "scope", "brand_id", "category_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_price_list_entries_variant",
                table: "price_list_entries",
                column: "variant_id");

            migrationBuilder.CreateIndex(
                name: "ux_price_list_entries_variant_min_qty",
                table: "price_list_entries",
                columns: new[] { "price_list_id", "variant_id", "min_qty" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_price_lists_company_status",
                table: "price_lists",
                columns: new[] { "company_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_price_lists_scope_status",
                table: "price_lists",
                columns: new[] { "scope", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_price_lists_one_open_version",
                table: "price_lists",
                column: "series_id",
                unique: true,
                filter: "status IN ('Draft', 'PendingApproval')");

            migrationBuilder.CreateIndex(
                name: "ux_price_lists_series_version",
                table: "price_lists",
                columns: new[] { "series_id", "version" },
                unique: true);

            // Immutability and four-eyes triggers, and the one global margin floor (10 %), idempotent.
            migrationBuilder.Sql(PriceListSql.Install);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "margin_floors");

            migrationBuilder.DropTable(
                name: "price_list_contributors");

            migrationBuilder.DropTable(
                name: "price_list_entries");

            migrationBuilder.DropTable(
                name: "price_lists");

            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS price_lists_guard();
                DROP FUNCTION IF EXISTS price_list_entries_guard();
                DROP FUNCTION IF EXISTS price_list_contributors_guard();
                """);
        }
    }
}
