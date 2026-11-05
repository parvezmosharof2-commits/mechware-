using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopVerse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompanyRejection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_companies_bin",
                table: "companies");

            migrationBuilder.AddColumn<string>(
                name: "rejection_reason",
                table: "companies",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_companies_bin",
                table: "companies",
                column: "bin",
                unique: true,
                filter: "status <> 'Rejected'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_companies_bin",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "rejection_reason",
                table: "companies");

            migrationBuilder.CreateIndex(
                name: "ux_companies_bin",
                table: "companies",
                column: "bin",
                unique: true);
        }
    }
}
