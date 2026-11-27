using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mechware.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// E6.5c + F2.3c: the replacement flag on credit-note requests and the replacement check at COMMIT (<see cref="CreditNoteSql"/>, ADR-0012 §9.4);
    /// the return's source key unique only among open or approved requests, so a rejected or withdrawn one frees the return to ask again.
    /// </summary>
    public partial class AddReplacementAndReturnRetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_credit_note_requests_source",
                table: "credit_note_requests");

            migrationBuilder.AddColumn<bool>(
                name: "issues_replacement",
                table: "credit_note_requests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ux_credit_note_requests_source",
                table: "credit_note_requests",
                column: "source_key",
                unique: true,
                filter: "source_key IS NOT NULL AND status IN ('Requested', 'Approved')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_note_requests_replacement",
                table: "credit_note_requests",
                sql: "NOT issues_replacement OR (reason = 'Error' AND source_key IS NULL)");

            migrationBuilder.Sql(CreditNoteSql.Install);   // idempotent: adds trg_invoice_replacement_check
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CreditNoteSql.UninstallReplacement);
            migrationBuilder.DropIndex(
                name: "ux_credit_note_requests_source",
                table: "credit_note_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_note_requests_replacement",
                table: "credit_note_requests");

            migrationBuilder.DropColumn(
                name: "issues_replacement",
                table: "credit_note_requests");

            migrationBuilder.CreateIndex(
                name: "ux_credit_note_requests_source",
                table: "credit_note_requests",
                column: "source_key",
                unique: true,
                filter: "source_key IS NOT NULL");
        }
    }
}
