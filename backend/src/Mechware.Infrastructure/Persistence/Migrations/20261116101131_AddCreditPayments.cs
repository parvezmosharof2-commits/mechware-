using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mechware.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCreditPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "credit_payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    reference = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    drawer_bank = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    received_on = table.Column<DateOnly>(type: "date", nullable: false),
                    cheque_date = table.Column<DateOnly>(type: "date", nullable: true),
                    late_entry_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    allocations = table.Column<string>(type: "jsonb", nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    recorded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    verified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    verified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cleared_on = table.Column<DateOnly>(type: "date", nullable: true),
                    rejected_by = table.Column<Guid>(type: "uuid", nullable: true),
                    rejected_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    bounced_by = table.Column<Guid>(type: "uuid", nullable: true),
                    bounced_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decision_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credit_payments", x => x.id);
                    table.CheckConstraint("ck_credit_payments_allocations", "jsonb_typeof(allocations) = 'array' AND jsonb_array_length(allocations) <= 50");
                    table.CheckConstraint("ck_credit_payments_amount", "amount_minor BETWEEN 1 AND 1000000000000 AND currency = 'BDT'");
                    table.CheckConstraint("ck_credit_payments_four_eyes", "(verified_by IS NULL OR verified_by <> recorded_by) AND (rejected_by IS NULL OR rejected_by <> recorded_by)");
                    table.CheckConstraint("ck_credit_payments_instrument", "(method IN ('cheque','pay_order','bank_deposit')) = (drawer_bank IS NOT NULL) AND (method IN ('cheque','pay_order')) = (cheque_date IS NOT NULL)");
                    table.CheckConstraint("ck_credit_payments_method", "method IN ('beftn','rtgs','npsb','bkash','nagad', 'cheque','pay_order','bank_deposit', 'cash')");
                    table.CheckConstraint("ck_credit_payments_status", "CASE status\nWHEN 'recorded' THEN num_nonnulls(verified_by, verified_at, cleared_on, rejected_by, rejected_at, bounced_by, bounced_at, decision_reason) = 0\nWHEN 'verified' THEN num_nonnulls(verified_by, verified_at, cleared_on) = 3\n    AND num_nonnulls(rejected_by, rejected_at, bounced_by, bounced_at, decision_reason) = 0\nWHEN 'rejected' THEN num_nonnulls(rejected_by, rejected_at, decision_reason) = 3\n    AND num_nonnulls(verified_by, verified_at, cleared_on, bounced_by, bounced_at) = 0\nWHEN 'bounced' THEN num_nonnulls(verified_by, verified_at, cleared_on, bounced_by, bounced_at, decision_reason) = 6\n    AND num_nonnulls(rejected_by, rejected_at) = 0 AND method <> 'cash'\nELSE false END");
                    table.CheckConstraint("ck_credit_payments_texts", "reference ~ '^[A-Z0-9]{4,40}$' AND (drawer_bank IS NULL OR drawer_bank ~ '^[A-Z0-9]{2,60}$')");
                    table.ForeignKey(
                        name: "FK_credit_payments_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credit_payments_users_recorded_by",
                        column: x => x.recorded_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credit_payments_users_verified_by",
                        column: x => x.verified_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "credit_adjustments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    credit_note_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payout_reference = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    rejected_by = table.Column<Guid>(type: "uuid", nullable: true),
                    rejected_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decision_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credit_adjustments", x => x.id);
                    table.CheckConstraint("ck_credit_adjustments_amount", "amount_minor BETWEEN -1000000000000 AND 1000000000000 AND currency = 'BDT' AND length(reason) >= 10");
                    table.CheckConstraint("ck_credit_adjustments_four_eyes", "approved_by IS NULL OR approved_by <> requested_by");
                    table.CheckConstraint("ck_credit_adjustments_kind", "CASE kind\nWHEN 'write_off' THEN amount_minor > 0 AND order_id IS NOT NULL AND num_nonnulls(payment_id, credit_note_id, payout_reference) = 0\nWHEN 'on_account_correction' THEN amount_minor <> 0 AND order_id IS NULL AND num_nonnulls(payment_id, credit_note_id) = 1\n    AND payout_reference IS NULL\nWHEN 'on_account_payout' THEN amount_minor > 0 AND order_id IS NULL AND num_nonnulls(payment_id, credit_note_id) = 1\n    AND payout_reference ~ '^[A-Z0-9]{4,40}$'\nELSE false END");
                    table.CheckConstraint("ck_credit_adjustments_status", "CASE status\nWHEN 'requested' THEN num_nonnulls(approved_by, approved_at, rejected_by, rejected_at, decision_reason) = 0\nWHEN 'approved' THEN num_nonnulls(approved_by, approved_at) = 2 AND num_nonnulls(rejected_by, rejected_at, decision_reason) = 0\nWHEN 'rejected' THEN num_nonnulls(rejected_by, rejected_at, decision_reason) = 3 AND num_nonnulls(approved_by, approved_at) = 0\nELSE false END");
                    table.ForeignKey(
                        name: "FK_credit_adjustments_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credit_adjustments_credit_payments_payment_id",
                        column: x => x.payment_id,
                        principalTable: "credit_payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credit_adjustments_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credit_adjustments_users_approved_by",
                        column: x => x.approved_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credit_adjustments_users_requested_by",
                        column: x => x.requested_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_credit_ledger_entries_adjustment_id",
                table: "credit_ledger_entries",
                column: "adjustment_id");

            migrationBuilder.CreateIndex(
                name: "IX_credit_ledger_entries_payment_id",
                table: "credit_ledger_entries",
                column: "payment_id");

            migrationBuilder.CreateIndex(
                name: "IX_credit_adjustments_approved_by",
                table: "credit_adjustments",
                column: "approved_by");

            migrationBuilder.CreateIndex(
                name: "ix_credit_adjustments_company",
                table: "credit_adjustments",
                columns: new[] { "company_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_credit_adjustments_order_id",
                table: "credit_adjustments",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "IX_credit_adjustments_payment_id",
                table: "credit_adjustments",
                column: "payment_id");

            migrationBuilder.CreateIndex(
                name: "IX_credit_adjustments_requested_by",
                table: "credit_adjustments",
                column: "requested_by");

            migrationBuilder.CreateIndex(
                name: "ix_credit_adjustments_status",
                table: "credit_adjustments",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_credit_payments_company",
                table: "credit_payments",
                columns: new[] { "company_id", "received_on" });

            migrationBuilder.CreateIndex(
                name: "ix_credit_payments_status",
                table: "credit_payments",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_credit_payments_verified_by",
                table: "credit_payments",
                column: "verified_by");

            migrationBuilder.CreateIndex(
                name: "ux_credit_payments_idempotency",
                table: "credit_payments",
                columns: new[] { "recorded_by", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_credit_payments_instrument_reference",
                table: "credit_payments",
                columns: new[] { "company_id", "drawer_bank", "reference" },
                unique: true,
                filter: "status <> 'rejected' AND method IN ('cheque','pay_order','bank_deposit')");

            migrationBuilder.CreateIndex(
                name: "ux_credit_payments_transfer_reference",
                table: "credit_payments",
                column: "reference",
                unique: true,
                filter: "status <> 'rejected' AND method IN ('beftn','rtgs','npsb','bkash','nagad')");

            migrationBuilder.AddForeignKey(
                name: "FK_credit_ledger_entries_credit_adjustments_adjustment_id",
                table: "credit_ledger_entries",
                column: "adjustment_id",
                principalTable: "credit_adjustments",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_credit_ledger_entries_credit_payments_payment_id",
                table: "credit_ledger_entries",
                column: "payment_id",
                principalTable: "credit_payments",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // B3.3b: payments and adjustments change only step by step and are never deleted (the tests' fixtures run the same SQL).
            migrationBuilder.Sql(CreditLedgerSql.InstallSettlement);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_credit_ledger_entries_credit_adjustments_adjustment_id",
                table: "credit_ledger_entries");

            migrationBuilder.DropForeignKey(
                name: "FK_credit_ledger_entries_credit_payments_payment_id",
                table: "credit_ledger_entries");

            migrationBuilder.DropTable(
                name: "credit_adjustments");

            migrationBuilder.DropTable(
                name: "credit_payments");

            migrationBuilder.DropIndex(
                name: "IX_credit_ledger_entries_adjustment_id",
                table: "credit_ledger_entries");

            migrationBuilder.DropIndex(
                name: "IX_credit_ledger_entries_payment_id",
                table: "credit_ledger_entries");

            // The guard's triggers went with their tables.
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS credit_settlement_guard();");
        }
    }
}
