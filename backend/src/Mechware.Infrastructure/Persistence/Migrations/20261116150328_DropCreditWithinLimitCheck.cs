using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mechware.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// B3.5 (ADR-0010 §1, §8.1): a bounce puts the debt back even above the limit, so the CHECK <c>credit_used &lt;= credit_limit</c>
    /// goes; <see cref="CreditLedgerSql.InstallLimitGuard"/> keeps every charge within the limit and the limit from being lowered
    /// below the credit used. Down fails (correctly) while a bounce keeps a company above its limit.
    /// </summary>
    public partial class DropCreditWithinLimitCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_companies_credit_within_limit",
                table: "companies");

            migrationBuilder.Sql(CreditLedgerSql.InstallLimitGuard);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CreditLedgerSql.DropLimitGuard);

            migrationBuilder.AddCheckConstraint(
                name: "ck_companies_credit_within_limit",
                table: "companies",
                sql: "credit_used <= credit_limit");
        }
    }
}
