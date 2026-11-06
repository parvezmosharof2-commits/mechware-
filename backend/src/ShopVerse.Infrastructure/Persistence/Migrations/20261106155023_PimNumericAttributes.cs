using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopVerse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PimNumericAttributes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_attribute_values_attribute_id",
                table: "attribute_values");

            migrationBuilder.AddColumn<decimal>(
                name: "numeric_max",
                table: "attribute_values",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "numeric_value",
                table: "attribute_values",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_attribute_values_numeric",
                table: "attribute_values",
                columns: new[] { "attribute_id", "numeric_value" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_attribute_values_numeric",
                table: "attribute_values",
                sql: "(numeric_value IS NULL AND numeric_max IS NULL) OR (numeric_value >= 0 AND numeric_max >= numeric_value)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_attribute_values_numeric",
                table: "attribute_values");

            migrationBuilder.DropCheckConstraint(
                name: "ck_attribute_values_numeric",
                table: "attribute_values");

            migrationBuilder.DropColumn(
                name: "numeric_max",
                table: "attribute_values");

            migrationBuilder.DropColumn(
                name: "numeric_value",
                table: "attribute_values");

            migrationBuilder.CreateIndex(
                name: "IX_attribute_values_attribute_id",
                table: "attribute_values",
                column: "attribute_id");
        }
    }
}
