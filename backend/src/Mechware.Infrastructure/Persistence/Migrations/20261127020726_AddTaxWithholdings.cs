using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mechware.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// E6.6a: the <c>tax_withholdings</c> table (VDS/AIT certificates) with its caps and guard (<see cref="TaxWithholdingSql"/>), and the B3
    /// ledger kind <c>withholding_applied</c> with its <c>withholding_id</c> reference (at most one line per certificate).
    /// </summary>
    public partial class AddTaxWithholdings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_ledger_kind",
                table: "credit_ledger_entries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_ledger_signs",
                table: "credit_ledger_entries");

            migrationBuilder.AddColumn<Guid>(
                name: "withholding_id",
                table: "credit_ledger_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tax_withholdings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    certificate_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    certificate_key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    certificate_date = table.Column<DateOnly>(type: "date", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    recorded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decision_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tax_withholdings", x => x.id);
                    table.CheckConstraint("ck_tax_withholdings_certificate", "certificate_number ~ '^[A-Z0-9][A-Z0-9 /-]{0,39}$' AND strpos(certificate_number, '  ') = 0 AND certificate_key = regexp_replace(certificate_number, '[^A-Z0-9]', '', 'g') AND certificate_key <> ''");
                    table.CheckConstraint("ck_tax_withholdings_kind", "kind IN ('Vds', 'Ait') AND amount_minor >= 1");
                    table.CheckConstraint("ck_tax_withholdings_status", "CASE status\nWHEN 'Recorded' THEN num_nonnulls(decided_by, decided_at, decision_reason) = 0\nWHEN 'Verified' THEN num_nonnulls(decided_by, decided_at) = 2 AND decision_reason IS NULL AND decided_by <> recorded_by\nWHEN 'Rejected' THEN num_nonnulls(decided_by, decided_at) = 2 AND decided_by <> recorded_by\n    AND char_length(btrim(decision_reason)) BETWEEN 5 AND 500\nELSE false END");
                    table.ForeignKey(
                        name: "FK_tax_withholdings_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tax_withholdings_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tax_withholdings_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tax_withholdings_users_decided_by",
                        column: x => x.decided_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tax_withholdings_users_recorded_by",
                        column: x => x.recorded_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_credit_ledger_withholding",
                table: "credit_ledger_entries",
                column: "withholding_id",
                unique: true,
                filter: "withholding_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_ledger_kind",
                table: "credit_ledger_entries",
                sql: "kind IN ('charge','payment_applied','payment_on_account','on_account_applied','payment_reversed','charge_reduced','write_off','on_account_correction','on_account_payout','withholding_applied')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_ledger_signs",
                table: "credit_ledger_entries",
                sql: "CASE kind\nWHEN 'charge' THEN receivable_delta_minor > 0 AND on_account_delta_minor = 0\nWHEN 'payment_applied' THEN receivable_delta_minor < 0 AND on_account_delta_minor = 0 AND payment_id IS NOT NULL\nWHEN 'payment_on_account' THEN receivable_delta_minor = 0 AND on_account_delta_minor > 0 AND payment_id IS NOT NULL\nWHEN 'on_account_applied' THEN receivable_delta_minor < 0 AND on_account_delta_minor = receivable_delta_minor\n    AND (payment_id IS NOT NULL OR credit_note_id IS NOT NULL)\nWHEN 'payment_reversed' THEN payment_id IS NOT NULL AND ((receivable_delta_minor > 0 AND on_account_delta_minor = 0)\n    OR (receivable_delta_minor = 0 AND on_account_delta_minor < 0))\nWHEN 'charge_reduced' THEN order_id IS NOT NULL AND ((receivable_delta_minor < 0 AND on_account_delta_minor = 0)\n    OR (receivable_delta_minor = 0 AND on_account_delta_minor > 0 AND (payment_id IS NOT NULL OR credit_note_id IS NOT NULL)))\nWHEN 'write_off' THEN receivable_delta_minor < 0 AND on_account_delta_minor = 0 AND adjustment_id IS NOT NULL\nWHEN 'on_account_correction' THEN receivable_delta_minor = 0 AND adjustment_id IS NOT NULL\n    AND (payment_id IS NOT NULL OR credit_note_id IS NOT NULL)\nWHEN 'on_account_payout' THEN receivable_delta_minor = 0 AND on_account_delta_minor < 0 AND adjustment_id IS NOT NULL\n    AND (payment_id IS NOT NULL OR credit_note_id IS NOT NULL)\nWHEN 'withholding_applied' THEN receivable_delta_minor < 0 AND on_account_delta_minor = 0 AND withholding_id IS NOT NULL\n    AND num_nonnulls(payment_id, adjustment_id, credit_note_id) = 0\nELSE false END");

            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_ledger_withholding",
                table: "credit_ledger_entries",
                sql: "withholding_id IS NULL OR kind = 'withholding_applied'");

            migrationBuilder.CreateIndex(
                name: "IX_tax_withholdings_decided_by",
                table: "tax_withholdings",
                column: "decided_by");

            migrationBuilder.CreateIndex(
                name: "ix_tax_withholdings_invoice",
                table: "tax_withholdings",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "IX_tax_withholdings_order_id",
                table: "tax_withholdings",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "IX_tax_withholdings_recorded_by",
                table: "tax_withholdings",
                column: "recorded_by");

            migrationBuilder.CreateIndex(
                name: "ix_tax_withholdings_status",
                table: "tax_withholdings",
                columns: new[] { "status", "decided_at" });

            migrationBuilder.CreateIndex(
                name: "ux_tax_withholdings_certificate",
                table: "tax_withholdings",
                columns: new[] { "company_id", "kind", "certificate_key" },
                unique: true,
                filter: "status <> 'Rejected'");

            migrationBuilder.AddForeignKey(
                name: "FK_credit_ledger_entries_tax_withholdings_withholding_id",
                table: "credit_ledger_entries",
                column: "withholding_id",
                principalTable: "tax_withholdings",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(TaxWithholdingSql.Install);   // caps at COMMIT under the invoice lock, guard (ADR-0012 §11, §16)
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(TaxWithholdingSql.Uninstall);

            migrationBuilder.DropForeignKey(
                name: "FK_credit_ledger_entries_tax_withholdings_withholding_id",
                table: "credit_ledger_entries");

            migrationBuilder.DropTable(
                name: "tax_withholdings");

            migrationBuilder.DropIndex(
                name: "ux_credit_ledger_withholding",
                table: "credit_ledger_entries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_ledger_kind",
                table: "credit_ledger_entries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_ledger_signs",
                table: "credit_ledger_entries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_ledger_withholding",
                table: "credit_ledger_entries");

            migrationBuilder.DropColumn(
                name: "withholding_id",
                table: "credit_ledger_entries");

            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_ledger_kind",
                table: "credit_ledger_entries",
                sql: "kind IN ('charge','payment_applied','payment_on_account','on_account_applied','payment_reversed','charge_reduced','write_off','on_account_correction','on_account_payout')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_ledger_signs",
                table: "credit_ledger_entries",
                sql: "CASE kind\nWHEN 'charge' THEN receivable_delta_minor > 0 AND on_account_delta_minor = 0\nWHEN 'payment_applied' THEN receivable_delta_minor < 0 AND on_account_delta_minor = 0 AND payment_id IS NOT NULL\nWHEN 'payment_on_account' THEN receivable_delta_minor = 0 AND on_account_delta_minor > 0 AND payment_id IS NOT NULL\nWHEN 'on_account_applied' THEN receivable_delta_minor < 0 AND on_account_delta_minor = receivable_delta_minor\n    AND (payment_id IS NOT NULL OR credit_note_id IS NOT NULL)\nWHEN 'payment_reversed' THEN payment_id IS NOT NULL AND ((receivable_delta_minor > 0 AND on_account_delta_minor = 0)\n    OR (receivable_delta_minor = 0 AND on_account_delta_minor < 0))\nWHEN 'charge_reduced' THEN order_id IS NOT NULL AND ((receivable_delta_minor < 0 AND on_account_delta_minor = 0)\n    OR (receivable_delta_minor = 0 AND on_account_delta_minor > 0 AND (payment_id IS NOT NULL OR credit_note_id IS NOT NULL)))\nWHEN 'write_off' THEN receivable_delta_minor < 0 AND on_account_delta_minor = 0 AND adjustment_id IS NOT NULL\nWHEN 'on_account_correction' THEN receivable_delta_minor = 0 AND adjustment_id IS NOT NULL\n    AND (payment_id IS NOT NULL OR credit_note_id IS NOT NULL)\nWHEN 'on_account_payout' THEN receivable_delta_minor = 0 AND on_account_delta_minor < 0 AND adjustment_id IS NOT NULL\n    AND (payment_id IS NOT NULL OR credit_note_id IS NOT NULL)\nELSE false END");
        }
    }
}
