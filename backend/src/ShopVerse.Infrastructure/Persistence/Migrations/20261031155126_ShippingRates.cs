using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopVerse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ShippingRates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "shipping_rates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    zone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    base_amount = table.Column<long>(type: "bigint", nullable: false),
                    per_kg_amount = table.Column<long>(type: "bigint", nullable: false),
                    volumetric_divisor = table.Column<int>(type: "integer", nullable: false),
                    weight_step_grams = table.Column<int>(type: "integer", nullable: false),
                    max_parcel_grams = table.Column<int>(type: "integer", nullable: true),
                    max_dimension_mm = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shipping_rates", x => x.id);
                    table.CheckConstraint("ck_shipping_rates_money", "base_amount >= 0 AND per_kg_amount >= 0");
                    table.CheckConstraint("ck_shipping_rates_units", "volumetric_divisor > 0 AND weight_step_grams > 0 AND (max_parcel_grams IS NULL OR max_parcel_grams > 0) AND (max_dimension_mm IS NULL OR max_dimension_mm > 0)");
                });

            migrationBuilder.CreateIndex(
                name: "ux_shipping_rates_zone",
                table: "shipping_rates",
                column: "zone",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "shipping_rates");
        }
    }
}
