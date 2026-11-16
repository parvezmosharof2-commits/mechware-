using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mechware.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCreditLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "credit_ledger_seq",
                table: "companies",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "credit_on_account",
                table: "companies",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "credit_ledger_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    adjustment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    credit_note_id = table.Column<Guid>(type: "uuid", nullable: true),
                    receivable_delta_minor = table.Column<long>(type: "bigint", nullable: false),
                    on_account_delta_minor = table.Column<long>(type: "bigint", nullable: false),
                    receivable_after_minor = table.Column<long>(type: "bigint", nullable: false),
                    on_account_after_minor = table.Column<long>(type: "bigint", nullable: false),
                    due_on = table.Column<DateOnly>(type: "date", nullable: true),
                    occurred_on = table.Column<DateOnly>(type: "date", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credit_ledger_entries", x => x.id);
                    table.CheckConstraint("ck_credit_ledger_currency", "currency = 'BDT'");
                    table.CheckConstraint("ck_credit_ledger_due_on", "(kind = 'charge') = (due_on IS NOT NULL)");
                    table.CheckConstraint("ck_credit_ledger_key", "length(idempotency_key) > 0");
                    table.CheckConstraint("ck_credit_ledger_kind", "kind IN ('charge','payment_applied','payment_on_account','on_account_applied','payment_reversed','charge_reduced','write_off','on_account_correction','on_account_payout')");
                    table.CheckConstraint("ck_credit_ledger_moves", "receivable_delta_minor <> 0 OR on_account_delta_minor <> 0");
                    table.CheckConstraint("ck_credit_ledger_no_adjustment_debt", "receivable_delta_minor <= 0 OR adjustment_id IS NULL");
                    table.CheckConstraint("ck_credit_ledger_receivable_order", "receivable_delta_minor = 0 OR order_id IS NOT NULL");
                    table.CheckConstraint("ck_credit_ledger_seq", "seq >= 1");
                    table.CheckConstraint("ck_credit_ledger_signs", "CASE kind\nWHEN 'charge' THEN receivable_delta_minor > 0 AND on_account_delta_minor = 0\nWHEN 'payment_applied' THEN receivable_delta_minor < 0 AND on_account_delta_minor = 0 AND payment_id IS NOT NULL\nWHEN 'payment_on_account' THEN receivable_delta_minor = 0 AND on_account_delta_minor > 0 AND payment_id IS NOT NULL\nWHEN 'on_account_applied' THEN receivable_delta_minor < 0 AND on_account_delta_minor = receivable_delta_minor\n    AND (payment_id IS NOT NULL OR credit_note_id IS NOT NULL)\nWHEN 'payment_reversed' THEN payment_id IS NOT NULL AND ((receivable_delta_minor > 0 AND on_account_delta_minor = 0)\n    OR (receivable_delta_minor = 0 AND on_account_delta_minor < 0))\nWHEN 'charge_reduced' THEN order_id IS NOT NULL AND ((receivable_delta_minor < 0 AND on_account_delta_minor = 0)\n    OR (receivable_delta_minor = 0 AND on_account_delta_minor > 0 AND (payment_id IS NOT NULL OR credit_note_id IS NOT NULL)))\nWHEN 'write_off' THEN receivable_delta_minor < 0 AND on_account_delta_minor = 0 AND adjustment_id IS NOT NULL\nWHEN 'on_account_correction' THEN receivable_delta_minor = 0 AND adjustment_id IS NOT NULL\n    AND (payment_id IS NOT NULL OR credit_note_id IS NOT NULL)\nWHEN 'on_account_payout' THEN receivable_delta_minor = 0 AND on_account_delta_minor < 0 AND adjustment_id IS NOT NULL\n    AND (payment_id IS NOT NULL OR credit_note_id IS NOT NULL)\nELSE false END");
                    table.CheckConstraint("ck_credit_ledger_totals", "receivable_after_minor >= 0 AND on_account_after_minor >= 0");
                    table.ForeignKey(
                        name: "FK_credit_ledger_entries_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credit_ledger_entries_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_companies_credit_ledger",
                table: "companies",
                sql: "credit_on_account >= 0 AND credit_ledger_seq >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_credit_ledger_company_order",
                table: "credit_ledger_entries",
                columns: new[] { "company_id", "order_id" });

            migrationBuilder.CreateIndex(
                name: "ix_credit_ledger_order",
                table: "credit_ledger_entries",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ux_credit_ledger_company_seq",
                table: "credit_ledger_entries",
                columns: new[] { "company_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_credit_ledger_idempotency_key",
                table: "credit_ledger_entries",
                column: "idempotency_key",
                unique: true);

            // B3.3 (ADR-0010 §2.3, §3): append-only + deferred chain/cached-total check, then one charge per credit order confirmed
            // before the ledger. The backfill stops the migration, naming the companies, if credit_used differs from those orders.
            migrationBuilder.Sql(CreditLedgerSql.Install);
            migrationBuilder.Sql(CreditLedgerSql.Backfill);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_companies_credit_check ON companies;");

            migrationBuilder.DropTable(
                name: "credit_ledger_entries");

            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS credit_ledger_check();
                DROP FUNCTION IF EXISTS credit_ledger_entries_append_only();
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_companies_credit_ledger",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "credit_ledger_seq",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "credit_on_account",
                table: "companies");
        }
    }
}
