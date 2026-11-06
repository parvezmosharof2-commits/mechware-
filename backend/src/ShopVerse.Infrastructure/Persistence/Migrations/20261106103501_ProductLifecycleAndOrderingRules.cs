using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopVerse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductLifecycleAndOrderingRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "lifecycle",
                table: "products",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "active");

            migrationBuilder.AddColumn<bool>(
                name: "successor_is_drop_in",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "successor_note",
                table: "products",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "successor_product_id",
                table: "products",
                type: "uuid",
                nullable: true);

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

            migrationBuilder.CreateTable(
                name: "product_alternatives",
                columns: table => new
                {
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alternative_product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_alternatives", x => new { x.product_id, x.alternative_product_id });
                    table.CheckConstraint("ck_product_alternatives_not_self", "product_id <> alternative_product_id");
                    table.ForeignKey(
                        name: "FK_product_alternatives_products_alternative_product_id",
                        column: x => x.alternative_product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_product_alternatives_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_products_successor_product_id",
                table: "products",
                column: "successor_product_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_variants_ordering_rules",
                table: "product_variants",
                sql: "pack_size >= 1 AND min_order_qty >= 1 AND min_order_qty % pack_size = 0");

            migrationBuilder.CreateIndex(
                name: "ix_product_alternatives_alternative",
                table: "product_alternatives",
                column: "alternative_product_id");

            migrationBuilder.AddForeignKey(
                name: "FK_products_products_successor_product_id",
                table: "products",
                column: "successor_product_id",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_products_products_successor_product_id",
                table: "products");

            migrationBuilder.DropTable(
                name: "product_alternatives");

            migrationBuilder.DropIndex(
                name: "IX_products_successor_product_id",
                table: "products");

            migrationBuilder.DropCheckConstraint(
                name: "ck_variants_ordering_rules",
                table: "product_variants");

            migrationBuilder.DropColumn(
                name: "lifecycle",
                table: "products");

            migrationBuilder.DropColumn(
                name: "successor_is_drop_in",
                table: "products");

            migrationBuilder.DropColumn(
                name: "successor_note",
                table: "products");

            migrationBuilder.DropColumn(
                name: "successor_product_id",
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
