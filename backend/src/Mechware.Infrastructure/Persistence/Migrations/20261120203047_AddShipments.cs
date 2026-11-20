using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mechware.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddShipments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "shipments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    courier_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    tracking_number = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    tracking_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    shipped_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    delivered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shipments", x => x.id);
                    table.CheckConstraint("ck_shipments_courier_name", "char_length(courier_name) BETWEEN 2 AND 80 AND courier_name !~ '[[:cntrl:]]' AND courier_name = btrim(courier_name)");
                    table.CheckConstraint("ck_shipments_delivered_after_shipped", "delivered_at IS NULL OR delivered_at >= shipped_at");
                    table.CheckConstraint("ck_shipments_tracking_number", "tracking_number ~ '^[A-Za-z0-9-]{1,60}$'");
                    table.CheckConstraint("ck_shipments_tracking_url", "tracking_url IS NULL OR (tracking_url LIKE 'https://%' AND char_length(tracking_url) <= 500)");
                    table.ForeignKey(
                        name: "FK_shipments_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_shipments_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_shipments_created_by",
                table: "shipments",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ux_shipments_order",
                table: "shipments",
                column: "order_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "shipments");
        }
    }
}
