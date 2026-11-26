using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mechware.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCreditNoteRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_invoice_lines_shape",
                table: "invoice_lines");

            migrationBuilder.CreateTable(
                name: "credit_note_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    lines = table.Column<string>(type: "jsonb", nullable: false),
                    total_minor = table.Column<long>(type: "bigint", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    source_key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decision_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    issued_invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credit_note_requests", x => x.id);
                    table.CheckConstraint("ck_credit_note_requests_key", "idempotency_key ~ '^[A-Za-z0-9_:-]{8,100}$' AND request_hash ~ '^[0-9a-f]{64}$' AND (source_key IS NULL OR source_key ~ '^return:[0-9a-f]{32}$')");
                    table.CheckConstraint("ck_credit_note_requests_lines", "jsonb_typeof(lines) = 'array' AND jsonb_array_length(lines) BETWEEN 1 AND 200 AND total_minor >= 1");
                    table.CheckConstraint("ck_credit_note_requests_reason", "reason IN ('GoodsReturned', 'PriceReduction', 'Error', 'Other') AND reason_text = btrim(reason_text) AND char_length(reason_text) BETWEEN 5 AND 500 AND reason_text !~ '[[:cntrl:]]' AND strpos(reason_text, '  ') = 0");
                    table.CheckConstraint("ck_credit_note_requests_status", "CASE status\r\nWHEN 'Requested' THEN num_nonnulls(decided_by, decided_at, decision_reason, issued_invoice_id) = 0\r\nWHEN 'Approved' THEN num_nonnulls(decided_by, decided_at, issued_invoice_id) = 3 AND decision_reason IS NULL AND decided_by <> requested_by\r\nWHEN 'Rejected' THEN num_nonnulls(decided_by, decided_at) = 2 AND issued_invoice_id IS NULL\r\n    AND char_length(btrim(decision_reason)) BETWEEN 5 AND 500\r\nWHEN 'Withdrawn' THEN decided_by = requested_by AND decided_at IS NOT NULL AND num_nonnulls(decision_reason, issued_invoice_id) = 0\r\nELSE false END");
                    table.ForeignKey(
                        name: "FK_credit_note_requests_invoices_issued_invoice_id",
                        column: x => x.issued_invoice_id,
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credit_note_requests_invoices_original_invoice_id",
                        column: x => x.original_invoice_id,
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credit_note_requests_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credit_note_requests_users_decided_by",
                        column: x => x.decided_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credit_note_requests_users_requested_by",
                        column: x => x.requested_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_invoice_lines_shape",
                table: "invoice_lines",
                sql: "line_no >= 1 AND (quantity >= 1 OR original_line_id IS NOT NULL AND quantity = 0) AND (original_line_id IS NULL OR net_minor >= 1) AND (kind = 'Goods' AND sku IS NOT NULL OR kind = 'Delivery' AND order_item_id IS NULL AND quantity <= 1 AND (quantity = 1 OR original_line_id IS NOT NULL)) AND char_length(btrim(description_en)) >= 1 AND char_length(btrim(description_bn)) >= 1 AND char_length(btrim(unit)) >= 1");

            migrationBuilder.CreateIndex(
                name: "IX_credit_note_requests_decided_by",
                table: "credit_note_requests",
                column: "decided_by");

            migrationBuilder.CreateIndex(
                name: "IX_credit_note_requests_order_id",
                table: "credit_note_requests",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_credit_note_requests_original",
                table: "credit_note_requests",
                column: "original_invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_credit_note_requests_status",
                table: "credit_note_requests",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_credit_note_requests_idempotency",
                table: "credit_note_requests",
                columns: new[] { "requested_by", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_credit_note_requests_issued",
                table: "credit_note_requests",
                column: "issued_invoice_id",
                unique: true,
                filter: "issued_invoice_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_credit_note_requests_source",
                table: "credit_note_requests",
                column: "source_key",
                unique: true,
                filter: "source_key IS NOT NULL");

            // The deferred credit-cap check on invoice_lines and the request guard (E6.5a, ADR-0012 §9.2).
            migrationBuilder.Sql(CreditNoteSql.Install);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CreditNoteSql.Uninstall);

            migrationBuilder.DropTable(
                name: "credit_note_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_invoice_lines_shape",
                table: "invoice_lines");

            migrationBuilder.AddCheckConstraint(
                name: "ck_invoice_lines_shape",
                table: "invoice_lines",
                sql: "line_no >= 1 AND quantity >= 1 AND (kind = 'Goods' AND sku IS NOT NULL OR kind = 'Delivery' AND order_item_id IS NULL AND quantity = 1) AND char_length(btrim(description_en)) >= 1 AND char_length(btrim(description_bn)) >= 1 AND char_length(btrim(unit)) >= 1");
        }
    }
}
