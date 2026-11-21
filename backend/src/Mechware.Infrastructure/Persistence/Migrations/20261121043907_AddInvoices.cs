using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mechware.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "invoices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    seller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    series = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    fiscal_year = table.Column<int>(type: "integer", nullable: false),
                    serial = table.Column<long>(type: "bigint", nullable: false),
                    issued_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sub_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    replacement_no = table.Column<int>(type: "integer", nullable: false),
                    original_invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    seller_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    buyer_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    delivery_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    purchase_order_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    quote_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    order_number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    net_minor = table.Column<long>(type: "bigint", nullable: false),
                    sd_minor = table.Column<long>(type: "bigint", nullable: false),
                    vat_minor = table.Column<long>(type: "bigint", nullable: false),
                    total_minor = table.Column<long>(type: "bigint", nullable: false),
                    consultant_checked = table.Column<bool>(type: "boolean", nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    issued_by = table.Column<Guid>(type: "uuid", nullable: false),
                    pdf_bn_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    pdf_bn_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    pdf_en_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    pdf_en_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    reason_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    refund_due_minor = table.Column<long>(type: "bigint", nullable: true),
                    replaces_invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoices", x => x.id);
                    table.CheckConstraint("ck_invoices_hash", "content_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_invoices_kind_series", "(kind = 'TaxInvoice' AND series IN ('TaxInvoice', 'SpecimenTaxInvoice') OR kind = 'CreditNote' AND series IN ('CreditNote', 'SpecimenCreditNote')) AND mode IN ('Live', 'Specimen') AND (mode = 'Specimen') = (series IN ('SpecimenTaxInvoice', 'SpecimenCreditNote'))");
                    table.CheckConstraint("ck_invoices_kind_shape", "(kind = 'TaxInvoice' AND shipment_id IS NOT NULL AND original_invoice_id IS NULL AND reason IS NULL AND reason_text IS NULL AND requested_by IS NULL AND refund_due_minor IS NULL) OR (kind = 'CreditNote' AND shipment_id IS NULL AND original_invoice_id IS NOT NULL AND reason ~ '^[A-Za-z]{2,40}$' AND requested_by IS NOT NULL AND requested_by <> issued_by AND refund_due_minor IS NOT NULL AND replacement_no = 0)");
                    table.CheckConstraint("ck_invoices_links", "replacement_no >= 0 AND (replacement_no = 0) = (replaces_invoice_id IS NULL) AND replaces_invoice_id IS DISTINCT FROM id AND original_invoice_id IS DISTINCT FROM id");
                    table.CheckConstraint("ck_invoices_money", "net_minor >= 0 AND sd_minor >= 0 AND vat_minor >= 0 AND total_minor = net_minor + sd_minor + vat_minor AND (refund_due_minor IS NULL OR refund_due_minor BETWEEN 0 AND total_minor)");
                    table.CheckConstraint("ck_invoices_number", "fiscal_year BETWEEN 2000 AND 9998 AND serial >= 1 AND split_part(number, '-', 1) ~ '^[A-Z]{2,6}$' AND (split_part(number, '-', 1) = 'SPEC') = (series IN ('SpecimenTaxInvoice', 'SpecimenCreditNote')) AND number = split_part(number, '-', 1) || CASE WHEN kind = 'TaxInvoice' THEN '-INV-' ELSE '-CN-' END || fiscal_year::text || '-' || lpad(((fiscal_year + 1) % 100)::text, 2, '0') || '-' || CASE WHEN serial < 1000000 THEN lpad(serial::text, 6, '0') ELSE serial::text END");
                    table.CheckConstraint("ck_invoices_pdf", "(pdf_en_key IS NULL) = (pdf_en_sha256 IS NULL) AND (pdf_bn_key IS NULL) = (pdf_bn_sha256 IS NULL) AND (pdf_en_sha256 IS NULL OR pdf_en_sha256 ~ '^[0-9a-f]{64}$') AND (pdf_bn_sha256 IS NULL OR pdf_bn_sha256 ~ '^[0-9a-f]{64}$')");
                    table.CheckConstraint("ck_invoices_texts", "currency ~ '^[A-Z]{3}$' AND char_length(order_number) BETWEEN 1 AND 16 AND (reason_text IS NULL OR char_length(reason_text) <= 500) AND jsonb_typeof(seller_snapshot) = 'object' AND jsonb_typeof(buyer_snapshot) = 'object' AND jsonb_typeof(delivery_snapshot) = 'object'");
                    table.ForeignKey(
                        name: "FK_invoices_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_invoices_invoices_original_invoice_id",
                        column: x => x.original_invoice_id,
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_invoices_invoices_replaces_invoice_id",
                        column: x => x.replaces_invoice_id,
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_invoices_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_invoices_sellers_seller_id",
                        column: x => x.seller_id,
                        principalTable: "sellers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_invoices_shipments_shipment_id",
                        column: x => x.shipment_id,
                        principalTable: "shipments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_invoices_sub_orders_sub_order_id",
                        column: x => x.sub_order_id,
                        principalTable: "sub_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_invoices_users_issued_by",
                        column: x => x.issued_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_invoices_users_requested_by",
                        column: x => x.requested_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "invoice_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    order_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    original_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    part_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    description_en = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    description_bn = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    unit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price_minor = table.Column<long>(type: "bigint", nullable: true),
                    display_unit_price_minor = table.Column<long>(type: "bigint", nullable: true),
                    gross_minor = table.Column<long>(type: "bigint", nullable: false),
                    discount_minor = table.Column<long>(type: "bigint", nullable: false),
                    net_minor = table.Column<long>(type: "bigint", nullable: false),
                    sd_rate = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    sd_minor = table.Column<long>(type: "bigint", nullable: false),
                    vat_rate = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    vat_minor = table.Column<long>(type: "bigint", nullable: false),
                    total_minor = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_lines", x => x.id);
                    table.CheckConstraint("ck_invoice_lines_money", "gross_minor >= 0 AND discount_minor >= 0 AND net_minor >= 0 AND sd_minor >= 0 AND vat_minor >= 0 AND total_minor = net_minor + sd_minor + vat_minor AND sd_rate BETWEEN 0 AND 1 AND vat_rate BETWEEN 0 AND 1");
                    table.CheckConstraint("ck_invoice_lines_shape", "line_no >= 1 AND quantity >= 1 AND (kind = 'Goods' AND sku IS NOT NULL OR kind = 'Delivery' AND order_item_id IS NULL AND quantity = 1) AND char_length(btrim(description_en)) >= 1 AND char_length(btrim(description_bn)) >= 1 AND char_length(btrim(unit)) >= 1");
                    table.CheckConstraint("ck_invoice_lines_unit_price", "(unit_price_minor IS NULL) <> (display_unit_price_minor IS NULL) AND COALESCE(unit_price_minor, display_unit_price_minor) >= 0");
                    table.ForeignKey(
                        name: "FK_invoice_lines_invoice_lines_original_line_id",
                        column: x => x.original_line_id,
                        principalTable: "invoice_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_invoice_lines_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_invoice_lines_order_items_order_item_id",
                        column: x => x.order_item_id,
                        principalTable: "order_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_order_item",
                table: "invoice_lines",
                column: "order_item_id",
                filter: "order_item_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_original",
                table: "invoice_lines",
                column: "original_line_id",
                filter: "original_line_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_invoice_lines_line_no",
                table: "invoice_lines",
                columns: new[] { "invoice_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_company_issued",
                table: "invoices",
                columns: new[] { "company_id", "issued_at" },
                filter: "company_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_issued_by",
                table: "invoices",
                column: "issued_by");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_order",
                table: "invoices",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_original",
                table: "invoices",
                column: "original_invoice_id",
                filter: "original_invoice_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_replaces_invoice_id",
                table: "invoices",
                column: "replaces_invoice_id");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_requested_by",
                table: "invoices",
                column: "requested_by");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_sub_order_id",
                table: "invoices",
                column: "sub_order_id");

            migrationBuilder.CreateIndex(
                name: "ux_invoices_number",
                table: "invoices",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_invoices_one_per_shipment",
                table: "invoices",
                columns: new[] { "shipment_id", "seller_id", "replacement_no" },
                unique: true,
                filter: "kind = 'TaxInvoice'");

            migrationBuilder.CreateIndex(
                name: "ux_invoices_serial",
                table: "invoices",
                columns: new[] { "seller_id", "series", "fiscal_year", "serial" },
                unique: true);

            // E6.3b guards and COMMIT checks (insert-only invoices and lines, counter step <-> invoice).
            migrationBuilder.Sql(InvoiceSql.Install);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(InvoiceSql.Uninstall);

            migrationBuilder.DropTable(
                name: "invoice_lines");

            migrationBuilder.DropTable(
                name: "invoices");
        }
    }
}
