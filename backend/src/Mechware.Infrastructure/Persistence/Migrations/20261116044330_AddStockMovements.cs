using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mechware.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStockMovements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stock_movements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    inventory_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delta = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reference_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    reference_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_movements", x => x.id);
                    table.CheckConstraint("ck_stock_movements_delta", "delta <> 0");
                    table.CheckConstraint("ck_stock_movements_reference", "length(reference_type) > 0");
                    table.ForeignKey(
                        name: "FK_stock_movements_inventory_items_inventory_item_id",
                        column: x => x.inventory_item_id,
                        principalTable: "inventory_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_item",
                table: "stock_movements",
                columns: new[] { "inventory_item_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_stock_movements_reference",
                table: "stock_movements",
                columns: new[] { "reference_type", "reference_id", "reason", "inventory_item_id" },
                unique: true);

            migrationBuilder.Sql(StockMovementSql.Install);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stock_movements");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS stock_movements_append_only();");
        }
    }
}
