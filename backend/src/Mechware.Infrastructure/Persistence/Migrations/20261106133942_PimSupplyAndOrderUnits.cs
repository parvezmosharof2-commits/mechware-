using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mechware.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PimSupplyAndOrderUnits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "successor_product_id",
                table: "products",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "supply_status",
                table: "products",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "active");

            migrationBuilder.AddColumn<int>(
                name: "min_order_qty",
                table: "product_variants",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "pack_size",
                table: "product_variants",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "ix_products_successor",
                table: "products",
                column: "successor_product_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_successor",
                table: "products",
                sql: "successor_product_id IS NULL OR (supply_status <> 'active' AND successor_product_id <> id)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_variants_order_units",
                table: "product_variants",
                sql: "pack_size >= 1 AND min_order_qty >= 1 AND min_order_qty <= 10000 AND min_order_qty % pack_size = 0");

            migrationBuilder.AddForeignKey(
                name: "FK_products_products_successor_product_id",
                table: "products",
                column: "successor_product_id",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_products_products_successor_product_id",
                table: "products");

            migrationBuilder.DropIndex(
                name: "ix_products_successor",
                table: "products");

            migrationBuilder.DropCheckConstraint(
                name: "ck_products_successor",
                table: "products");

            migrationBuilder.DropCheckConstraint(
                name: "ck_variants_order_units",
                table: "product_variants");

            migrationBuilder.DropColumn(
                name: "successor_product_id",
                table: "products");

            migrationBuilder.DropColumn(
                name: "supply_status",
                table: "products");

            migrationBuilder.DropColumn(
                name: "min_order_qty",
                table: "product_variants");

            migrationBuilder.DropColumn(
                name: "pack_size",
                table: "product_variants");
        }
    }
}
