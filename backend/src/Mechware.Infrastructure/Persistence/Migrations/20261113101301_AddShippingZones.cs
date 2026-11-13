using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mechware.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddShippingZones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "shipping_zones",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name_en = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name_bn = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    free_shipping_threshold_minor = table.Column<long>(type: "bigint", nullable: true),
                    heavy_unit_grams = table.Column<int>(type: "integer", nullable: true),
                    oversize_dimension_mm = table.Column<int>(type: "integer", nullable: true),
                    max_fee_minor = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shipping_zones", x => x.id);
                    table.CheckConstraint("ck_shipping_zones_free_threshold", "free_shipping_threshold_minor IS NULL OR free_shipping_threshold_minor > 0");
                    table.CheckConstraint("ck_shipping_zones_max_fee", "max_fee_minor > 0");
                });

            migrationBuilder.CreateTable(
                name: "shipping_rate_bands",
                columns: table => new
                {
                    zone_id = table.Column<Guid>(type: "uuid", nullable: false),
                    max_grams = table.Column<int>(type: "integer", nullable: false),
                    fee_minor = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shipping_rate_bands", x => new { x.zone_id, x.max_grams });
                    table.CheckConstraint("ck_shipping_rate_bands_fee", "fee_minor >= 0");
                    table.CheckConstraint("ck_shipping_rate_bands_grams", "max_grams > 0");
                    table.ForeignKey(
                        name: "FK_shipping_rate_bands_shipping_zones_zone_id",
                        column: x => x.zone_id,
                        principalTable: "shipping_zones",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "shipping_zone_districts",
                columns: table => new
                {
                    district_id = table.Column<Guid>(type: "uuid", nullable: false),
                    zone_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shipping_zone_districts", x => x.district_id);
                    table.ForeignKey(
                        name: "FK_shipping_zone_districts_bd_locations_district_id",
                        column: x => x.district_id,
                        principalTable: "bd_locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_shipping_zone_districts_shipping_zones_zone_id",
                        column: x => x.zone_id,
                        principalTable: "shipping_zones",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_shipping_zone_districts_zone",
                table: "shipping_zone_districts",
                column: "zone_id");

            migrationBuilder.CreateIndex(
                name: "ux_shipping_zones_code",
                table: "shipping_zones",
                column: "code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "shipping_rate_bands");

            migrationBuilder.DropTable(
                name: "shipping_zone_districts");

            migrationBuilder.DropTable(
                name: "shipping_zones");
        }
    }
}
