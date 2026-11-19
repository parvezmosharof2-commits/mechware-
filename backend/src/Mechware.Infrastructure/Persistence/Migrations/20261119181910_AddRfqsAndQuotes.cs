using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mechware.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRfqsAndQuotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rfqs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    needed_by = table.Column<DateOnly>(type: "date", nullable: true),
                    ship_to_site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    line_count = table.Column<int>(type: "integer", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    assigned_to = table.Column<Guid>(type: "uuid", nullable: true),
                    first_quote_sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    closed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    close_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    withdrawn_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rfqs", x => x.id);
                    table.CheckConstraint("ck_rfqs_answered", "(status IN ('Submitted', 'Quoting') AND first_quote_sent_at IS NULL) OR (status = 'Answered' AND first_quote_sent_at IS NOT NULL) OR status IN ('Closed', 'Withdrawn')");
                    table.CheckConstraint("ck_rfqs_closed", "(status = 'Closed') = (closed_at IS NOT NULL) AND (closed_at IS NULL) = (closed_by IS NULL) AND (closed_at IS NULL) = (close_reason IS NULL) AND (close_reason IS NULL OR char_length(close_reason) BETWEEN 5 AND 500)");
                    table.CheckConstraint("ck_rfqs_number", "number ~ '^MWR-[0-9]{6}-[A-Z2-7]{6}$'");
                    table.CheckConstraint("ck_rfqs_source", "source IN ('Cart', 'Text', 'Csv', 'Product')");
                    table.CheckConstraint("ck_rfqs_status", "status IN ('Submitted', 'Quoting', 'Answered', 'Closed', 'Withdrawn')");
                    table.CheckConstraint("ck_rfqs_texts", "(message IS NULL OR char_length(message) <= 2000) AND char_length(idempotency_key) BETWEEN 8 AND 100 AND request_hash ~ '^[0-9a-f]{64}$' AND line_count BETWEEN 1 AND 500");
                    table.CheckConstraint("ck_rfqs_withdrawn", "(status = 'Withdrawn') = (withdrawn_at IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_rfqs_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_rfqs_users_assigned_to",
                        column: x => x.assigned_to,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_rfqs_users_requested_by",
                        column: x => x.requested_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "quotes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rfq_id = table.Column<Guid>(type: "uuid", nullable: true),
                    contact_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_to = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    latest_version_no = table.Column<int>(type: "integer", nullable: false),
                    closed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    close_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quotes", x => x.id);
                    table.CheckConstraint("ck_quotes_closed", "(closed_at IS NULL) = (closed_by IS NULL) AND (closed_at IS NULL) = (close_reason IS NULL) AND (close_reason IS NULL OR char_length(close_reason) BETWEEN 5 AND 500)");
                    table.CheckConstraint("ck_quotes_latest_version", "latest_version_no >= 1");
                    table.CheckConstraint("ck_quotes_number", "number ~ '^MWQ-[0-9]{6}-[A-Z2-7]{6}$'");
                    table.ForeignKey(
                        name: "FK_quotes_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_quotes_rfqs_rfq_id",
                        column: x => x.rfq_id,
                        principalTable: "rfqs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_quotes_users_assigned_to",
                        column: x => x.assigned_to,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_quotes_users_contact_user_id",
                        column: x => x.contact_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "rfq_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rfq_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    raw_text = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    part_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    match_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    successor_variant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rfq_lines", x => x.id);
                    table.CheckConstraint("ck_rfq_lines_line", "line_no BETWEEN 1 AND 500 AND quantity BETWEEN 1 AND 10000");
                    table.CheckConstraint("ck_rfq_lines_match_status", "match_status IN ('Matched', 'Ambiguous', 'NotFound', 'Obsolete')");
                    table.CheckConstraint("ck_rfq_lines_texts", "char_length(raw_text) BETWEEN 1 AND 200 AND (part_key IS NULL OR char_length(part_key) <= 64) AND (note IS NULL OR char_length(note) <= 500)");
                    table.CheckConstraint("ck_rfq_lines_variant", "(variant_id IS NOT NULL) = (match_status IN ('Matched', 'Obsolete')) AND (successor_variant_id IS NULL OR match_status = 'Obsolete')");
                    table.ForeignKey(
                        name: "FK_rfq_lines_product_variants_successor_variant_id",
                        column: x => x.successor_variant_id,
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_rfq_lines_product_variants_variant_id",
                        column: x => x.variant_id,
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_rfq_lines_rfqs_rfq_id",
                        column: x => x.rfq_id,
                        principalTable: "rfqs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "quote_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    quote_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_no = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    validity_days = table.Column<int>(type: "integer", nullable: false),
                    payment_terms = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    shipping_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ship_to_site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    shipping_fees = table.Column<string>(type: "jsonb", nullable: true),
                    terms_text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    buyer_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    internal_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    company_snapshot = table.Column<string>(type: "jsonb", nullable: true),
                    preparer_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    flags = table.Column<string>(type: "jsonb", nullable: true),
                    goods_net_minor = table.Column<long>(type: "bigint", nullable: false),
                    tax_minor = table.Column<long>(type: "bigint", nullable: false),
                    goods_total_minor = table.Column<long>(type: "bigint", nullable: false),
                    shipping_total_minor = table.Column<long>(type: "bigint", nullable: true),
                    grand_total_minor = table.Column<long>(type: "bigint", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    submitted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    approval_comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    approval_rejected_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approval_rejected_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    sent_by = table.Column<Guid>(type: "uuid", nullable: true),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    valid_until_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    accepted_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    accepted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    accepted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    rejected_by = table.Column<Guid>(type: "uuid", nullable: true),
                    rejected_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reject_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    withdrawn_by = table.Column<Guid>(type: "uuid", nullable: true),
                    withdrawn_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    withdraw_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    superseded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    pdf_bn_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    pdf_bn_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    pdf_en_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    pdf_en_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    ship_to = table.Column<string>(type: "jsonb", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quote_versions", x => x.id);
                    table.CheckConstraint("ck_quote_versions_approval_shape", "(approved_by IS NULL) = (approved_at IS NULL) AND (approved_by IS NULL OR status IN ('Sent', 'Accepted', 'Rejected', 'Withdrawn', 'Superseded')) AND (approval_rejected_by IS NULL) = (approval_rejected_at IS NULL)");
                    table.CheckConstraint("ck_quote_versions_four_eyes", "approved_by IS NULL OR (submitted_by IS NOT NULL AND approved_by <> created_by AND approved_by <> submitted_by)");
                    table.CheckConstraint("ck_quote_versions_frozen", "content_hash IS NULL OR content_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_quote_versions_frozen_shape", "(status = 'Draft') = (content_hash IS NULL) AND (content_hash IS NULL) = (submitted_by IS NULL) AND (submitted_by IS NULL) = (submitted_at IS NULL) AND (content_hash IS NULL) = (flags IS NULL) AND (status = 'Draft' OR (company_snapshot IS NOT NULL AND preparer_name IS NOT NULL))");
                    table.CheckConstraint("ck_quote_versions_header", "version_no >= 1 AND validity_days BETWEEN 1 AND 365 AND payment_terms IN ('AsAccount', 'Advance') AND shipping_mode IN ('Calculated', 'Fixed')");
                    table.CheckConstraint("ck_quote_versions_money", "goods_net_minor >= 0 AND tax_minor >= 0 AND goods_total_minor = goods_net_minor + tax_minor AND (shipping_total_minor IS NULL) = (grand_total_minor IS NULL) AND (shipping_total_minor IS NULL OR (shipping_total_minor >= 0 AND grand_total_minor = goods_total_minor + shipping_total_minor))");
                    table.CheckConstraint("ck_quote_versions_outcome_shape", "(accepted_order_id IS NULL) = (accepted_by IS NULL) AND (accepted_order_id IS NULL) = (accepted_at IS NULL) AND (status <> 'Accepted' OR accepted_order_id IS NOT NULL) AND (accepted_order_id IS NULL OR status IN ('Accepted', 'Superseded')) AND (status = 'Rejected') = (rejected_by IS NOT NULL) AND (rejected_by IS NULL) = (rejected_at IS NULL) AND (status = 'Withdrawn') = (withdrawn_by IS NOT NULL) AND (withdrawn_by IS NULL) = (withdrawn_at IS NULL) AND (withdrawn_by IS NULL) = (withdraw_reason IS NULL) AND (status = 'Superseded') = (superseded_at IS NOT NULL)");
                    table.CheckConstraint("ck_quote_versions_pdf", "(pdf_en_key IS NULL) = (pdf_en_sha256 IS NULL) AND (pdf_bn_key IS NULL) = (pdf_bn_sha256 IS NULL) AND (pdf_en_sha256 IS NULL OR pdf_en_sha256 ~ '^[0-9a-f]{64}$') AND (pdf_bn_sha256 IS NULL OR pdf_bn_sha256 ~ '^[0-9a-f]{64}$') AND (pdf_en_key IS NULL AND pdf_bn_key IS NULL OR status IN ('Sent', 'Accepted', 'Rejected', 'Withdrawn', 'Superseded'))");
                    table.CheckConstraint("ck_quote_versions_sent_shape", "(status IN ('Sent', 'Accepted', 'Rejected', 'Withdrawn', 'Superseded')) = (sent_at IS NOT NULL) AND (sent_at IS NULL) = (sent_by IS NULL) AND (sent_at IS NULL) = (valid_until_utc IS NULL) AND (valid_until_utc IS NULL OR valid_until_utc > sent_at)");
                    table.CheckConstraint("ck_quote_versions_shipping", "(shipping_mode = 'Calculated' AND ship_to_site_id IS NULL AND shipping_fees IS NULL AND ship_to IS NULL AND shipping_total_minor IS NULL) OR (shipping_mode = 'Fixed' AND ship_to_site_id IS NOT NULL AND shipping_fees IS NOT NULL AND jsonb_typeof(shipping_fees) = 'array' AND (status = 'Draft' OR grand_total_minor IS NOT NULL))");
                    table.CheckConstraint("ck_quote_versions_status", "status IN ('Draft', 'PendingApproval', 'Sent', 'Accepted', 'Rejected', 'Withdrawn', 'Superseded')");
                    table.CheckConstraint("ck_quote_versions_texts", "(terms_text IS NULL OR char_length(terms_text) <= 4000) AND (buyer_note IS NULL OR char_length(buyer_note) <= 2000) AND (internal_note IS NULL OR char_length(internal_note) <= 2000) AND (preparer_name IS NULL OR char_length(preparer_name) BETWEEN 1 AND 200) AND (approval_comment IS NULL OR char_length(approval_comment) <= 500) AND (reject_reason IS NULL OR char_length(reject_reason) <= 1000) AND (withdraw_reason IS NULL OR char_length(withdraw_reason) BETWEEN 5 AND 500)");
                    table.ForeignKey(
                        name: "FK_quote_versions_orders_accepted_order_id",
                        column: x => x.accepted_order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_quote_versions_quotes_quote_id",
                        column: x => x.quote_id,
                        principalTable: "quotes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "quote_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rfq_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price_minor = table.Column<long>(type: "bigint", nullable: false),
                    line_note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    below_floor_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    seller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    part_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    title_en = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    title_bn = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    vat_rate = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    public_unit_price_minor = table.Column<long>(type: "bigint", nullable: false),
                    line_net_minor = table.Column<long>(type: "bigint", nullable: false),
                    line_tax_minor = table.Column<long>(type: "bigint", nullable: false),
                    line_total_minor = table.Column<long>(type: "bigint", nullable: false),
                    reference_unit_price_minor = table.Column<long>(type: "bigint", nullable: true),
                    reference_source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    cost_minor_at_check = table.Column<long>(type: "bigint", nullable: true),
                    floor_minor_at_check = table.Column<long>(type: "bigint", nullable: true),
                    below_floor = table.Column<bool>(type: "boolean", nullable: false),
                    over_authority = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quote_lines", x => x.id);
                    table.CheckConstraint("ck_quote_lines_below_floor_reason", "(NOT below_floor OR below_floor_reason IS NOT NULL) AND (below_floor_reason IS NULL OR char_length(below_floor_reason) BETWEEN 5 AND 500)");
                    table.CheckConstraint("ck_quote_lines_check", "(reference_unit_price_minor IS NULL) = (reference_source IS NULL) AND (reference_unit_price_minor IS NULL OR reference_unit_price_minor > 0) AND (reference_source IS NULL OR reference_source IN ('List', 'Sale', 'QuantityBreak', 'GroupList', 'Contract')) AND (cost_minor_at_check IS NULL OR cost_minor_at_check > 0) AND (floor_minor_at_check IS NULL OR floor_minor_at_check > 0)");
                    table.CheckConstraint("ck_quote_lines_line", "line_no BETWEEN 1 AND 50 AND quantity BETWEEN 1 AND 10000 AND unit_price_minor BETWEEN 1 AND 100000000000");
                    table.CheckConstraint("ck_quote_lines_money", "line_net_minor = quantity::bigint * unit_price_minor AND line_tax_minor >= 0 AND line_total_minor = line_net_minor + line_tax_minor");
                    table.CheckConstraint("ck_quote_lines_snapshot", "vat_rate BETWEEN 0 AND 1 AND public_unit_price_minor > 0 AND char_length(sku) BETWEEN 1 AND 64 AND (part_number IS NULL OR char_length(part_number) <= 64) AND char_length(title_en) BETWEEN 1 AND 255 AND char_length(title_bn) BETWEEN 1 AND 255 AND (line_note IS NULL OR char_length(line_note) <= 300)");
                    table.ForeignKey(
                        name: "FK_quote_lines_product_variants_variant_id",
                        column: x => x.variant_id,
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_quote_lines_quote_versions_version_id",
                        column: x => x.version_id,
                        principalTable: "quote_versions",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_quote_lines_rfq_lines_rfq_line_id",
                        column: x => x.rfq_line_id,
                        principalTable: "rfq_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_quote_lines_sellers_seller_id",
                        column: x => x.seller_id,
                        principalTable: "sellers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "quote_version_contributors",
                columns: table => new
                {
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_contributed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quote_version_contributors", x => new { x.version_id, x.user_id });
                    table.ForeignKey(
                        name: "FK_quote_version_contributors_quote_versions_version_id",
                        column: x => x.version_id,
                        principalTable: "quote_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_quote_lines_rfq_line_id",
                table: "quote_lines",
                column: "rfq_line_id");

            migrationBuilder.CreateIndex(
                name: "IX_quote_lines_seller_id",
                table: "quote_lines",
                column: "seller_id");

            migrationBuilder.CreateIndex(
                name: "ix_quote_lines_variant",
                table: "quote_lines",
                column: "variant_id");

            migrationBuilder.CreateIndex(
                name: "ux_quote_lines_line_no",
                table: "quote_lines",
                columns: new[] { "version_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_quote_lines_variant",
                table: "quote_lines",
                columns: new[] { "version_id", "variant_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quote_versions_accepted_order",
                table: "quote_versions",
                column: "accepted_order_id",
                filter: "accepted_order_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_quote_versions_approval_queue",
                table: "quote_versions",
                column: "submitted_at",
                filter: "status = 'PendingApproval'");

            migrationBuilder.CreateIndex(
                name: "ix_quote_versions_expiry",
                table: "quote_versions",
                column: "valid_until_utc",
                filter: "status = 'Sent'");

            migrationBuilder.CreateIndex(
                name: "ux_quote_versions_one_open",
                table: "quote_versions",
                column: "quote_id",
                unique: true,
                filter: "status IN ('Draft', 'PendingApproval')");

            migrationBuilder.CreateIndex(
                name: "ux_quote_versions_version_no",
                table: "quote_versions",
                columns: new[] { "quote_id", "version_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quotes_company_created",
                table: "quotes",
                columns: new[] { "company_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_quotes_contact_user_id",
                table: "quotes",
                column: "contact_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_quotes_open_assigned",
                table: "quotes",
                column: "assigned_to",
                filter: "closed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_quotes_rfq",
                table: "quotes",
                column: "rfq_id");

            migrationBuilder.CreateIndex(
                name: "ux_quotes_number",
                table: "quotes",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rfq_lines_successor_variant_id",
                table: "rfq_lines",
                column: "successor_variant_id");

            migrationBuilder.CreateIndex(
                name: "IX_rfq_lines_variant_id",
                table: "rfq_lines",
                column: "variant_id");

            migrationBuilder.CreateIndex(
                name: "ux_rfq_lines_line_no",
                table: "rfq_lines",
                columns: new[] { "rfq_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rfqs_assigned_to",
                table: "rfqs",
                column: "assigned_to");

            migrationBuilder.CreateIndex(
                name: "ix_rfqs_company_created",
                table: "rfqs",
                columns: new[] { "company_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_rfqs_inbox",
                table: "rfqs",
                column: "created_at",
                filter: "status IN ('Submitted', 'Quoting')");

            migrationBuilder.CreateIndex(
                name: "ix_rfqs_requester_created",
                table: "rfqs",
                columns: new[] { "requested_by", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ux_rfqs_idempotency",
                table: "rfqs",
                columns: new[] { "requested_by", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_rfqs_number",
                table: "rfqs",
                column: "number",
                unique: true);

            // Guard triggers, TRUNCATE refusals and the deferred "one live version" constraint (ADR-0011 §6, §8.6, §11).
            migrationBuilder.Sql(QuoteSql.Install);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "quote_lines");

            migrationBuilder.DropTable(
                name: "quote_version_contributors");

            migrationBuilder.DropTable(
                name: "rfq_lines");

            migrationBuilder.DropTable(
                name: "quote_versions");

            migrationBuilder.DropTable(
                name: "quotes");

            migrationBuilder.DropTable(
                name: "rfqs");

            // The tables took their triggers and the exclusion constraint with them; the functions stay until dropped.
            migrationBuilder.Sql(QuoteSql.Uninstall);
        }
    }
}
