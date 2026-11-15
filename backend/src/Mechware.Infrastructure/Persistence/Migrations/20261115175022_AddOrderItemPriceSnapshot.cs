using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mechware.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderItemPriceSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "contract_ref",
                table: "order_items",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "price_basis",
                table: "order_items",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "price_list_entry_id",
                table: "order_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "price_list_id",
                table: "order_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "price_list_version",
                table: "order_items",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "price_source",
                table: "order_items",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "public_unit_price_minor",
                table: "order_items",
                type: "bigint",
                nullable: true);

            // Lines placed before the snapshot (ADR-0008 §8): the basis is certain from the order's tax mode. The source is
            // not (a list price and a sale price were never told apart), so it stays NULL rather than claiming "List".
            migrationBuilder.Sql("""
                UPDATE order_items AS i
                SET price_basis = CASE o.tax_mode WHEN 'Inclusive' THEN 'GrossInclVat' ELSE 'NetOfVat' END
                FROM sub_orders AS s JOIN orders AS o ON o.id = s.order_id
                WHERE i.sub_order_id = s.id AND i.price_basis IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_order_items_price_list",
                table: "order_items",
                column: "price_list_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_items_price_list_entry",
                table: "order_items",
                column: "price_list_entry_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_order_items_price_snapshot",
                table: "order_items",
                sql: "(price_basis IS NULL OR price_basis IN ('GrossInclVat', 'NetOfVat')) AND (price_source IS NULL OR (price_source IN ('List', 'Sale', 'QuantityBreak', 'GroupList', 'Contract', 'Quote') AND price_basis IS NOT NULL AND COALESCE(public_unit_price_minor, 0) > 0)) AND (price_list_id IS NULL) = (price_list_version IS NULL) AND (price_list_id IS NULL) = (price_list_entry_id IS NULL) AND (price_list_id IS NULL) = (price_source IS NULL OR price_source NOT IN ('QuantityBreak', 'GroupList', 'Contract')) AND (contract_ref IS NULL) = (price_source IS DISTINCT FROM 'Contract') AND (public_unit_price_minor IS NULL OR price_source IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_order_items_price_list_entries_price_list_entry_id",
                table: "order_items",
                column: "price_list_entry_id",
                principalTable: "price_list_entries",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_order_items_price_lists_price_list_id",
                table: "order_items",
                column: "price_list_id",
                principalTable: "price_lists",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_order_items_price_list_entries_price_list_entry_id",
                table: "order_items");

            migrationBuilder.DropForeignKey(
                name: "FK_order_items_price_lists_price_list_id",
                table: "order_items");

            migrationBuilder.DropIndex(
                name: "ix_order_items_price_list",
                table: "order_items");

            migrationBuilder.DropIndex(
                name: "ix_order_items_price_list_entry",
                table: "order_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_order_items_price_snapshot",
                table: "order_items");

            migrationBuilder.DropColumn(
                name: "contract_ref",
                table: "order_items");

            migrationBuilder.DropColumn(
                name: "price_basis",
                table: "order_items");

            migrationBuilder.DropColumn(
                name: "price_list_entry_id",
                table: "order_items");

            migrationBuilder.DropColumn(
                name: "price_list_id",
                table: "order_items");

            migrationBuilder.DropColumn(
                name: "price_list_version",
                table: "order_items");

            migrationBuilder.DropColumn(
                name: "price_source",
                table: "order_items");

            migrationBuilder.DropColumn(
                name: "public_unit_price_minor",
                table: "order_items");
        }
    }
}
