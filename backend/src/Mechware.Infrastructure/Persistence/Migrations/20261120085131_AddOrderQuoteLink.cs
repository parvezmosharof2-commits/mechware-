using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mechware.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderQuoteLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_order_items_price_snapshot",
                table: "order_items");

            migrationBuilder.AddColumn<Guid>(
                name: "quote_id",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "quote_version_id",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "quote_line_id",
                table: "order_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_quote",
                table: "orders",
                column: "quote_id",
                filter: "quote_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_orders_live_quote_version",
                table: "orders",
                column: "quote_version_id",
                unique: true,
                filter: "quote_version_id IS NOT NULL AND status <> 'Cancelled'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_quote_link",
                table: "orders",
                sql: "(quote_id IS NULL) = (quote_version_id IS NULL) AND (quote_id IS NULL OR company_id IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_order_items_quote_line",
                table: "order_items",
                column: "quote_line_id",
                filter: "quote_line_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_order_items_price_snapshot",
                table: "order_items",
                sql: "(price_basis IS NULL OR price_basis IN ('GrossInclVat', 'NetOfVat')) AND (price_source IS NULL OR (price_source IN ('List', 'Sale', 'QuantityBreak', 'GroupList', 'Contract', 'Quote') AND price_basis IS NOT NULL AND COALESCE(public_unit_price_minor, 0) > 0)) AND (price_list_id IS NULL) = (price_list_version IS NULL) AND (price_list_id IS NULL) = (price_list_entry_id IS NULL) AND (price_list_id IS NULL) = (price_source IS NULL OR price_source NOT IN ('QuantityBreak', 'GroupList', 'Contract')) AND (contract_ref IS NULL) = (price_source IS DISTINCT FROM 'Contract') AND (public_unit_price_minor IS NULL OR price_source IS NOT NULL) AND (quote_line_id IS NULL) = (price_source IS DISTINCT FROM 'Quote')");

            migrationBuilder.AddForeignKey(
                name: "FK_order_items_quote_lines_quote_line_id",
                table: "order_items",
                column: "quote_line_id",
                principalTable: "quote_lines",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_orders_quote_versions_quote_version_id",
                table: "orders",
                column: "quote_version_id",
                principalTable: "quote_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_orders_quotes_quote_id",
                table: "orders",
                column: "quote_id",
                principalTable: "quotes",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_order_items_quote_lines_quote_line_id",
                table: "order_items");

            migrationBuilder.DropForeignKey(
                name: "FK_orders_quote_versions_quote_version_id",
                table: "orders");

            migrationBuilder.DropForeignKey(
                name: "FK_orders_quotes_quote_id",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "ix_orders_quote",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "ux_orders_live_quote_version",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_quote_link",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "ix_order_items_quote_line",
                table: "order_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_order_items_price_snapshot",
                table: "order_items");

            migrationBuilder.DropColumn(
                name: "quote_id",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "quote_version_id",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "quote_line_id",
                table: "order_items");

            migrationBuilder.AddCheckConstraint(
                name: "ck_order_items_price_snapshot",
                table: "order_items",
                sql: "(price_basis IS NULL OR price_basis IN ('GrossInclVat', 'NetOfVat')) AND (price_source IS NULL OR (price_source IN ('List', 'Sale', 'QuantityBreak', 'GroupList', 'Contract', 'Quote') AND price_basis IS NOT NULL AND COALESCE(public_unit_price_minor, 0) > 0)) AND (price_list_id IS NULL) = (price_list_version IS NULL) AND (price_list_id IS NULL) = (price_list_entry_id IS NULL) AND (price_list_id IS NULL) = (price_source IS NULL OR price_source NOT IN ('QuantityBreak', 'GroupList', 'Contract')) AND (contract_ref IS NULL) = (price_source IS DISTINCT FROM 'Contract') AND (public_unit_price_minor IS NULL OR price_source IS NOT NULL)");
        }
    }
}
