using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopVerse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "payment_due_at",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "pays_on_credit",
                table: "orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Orders already waiting for payment keep their 30 minutes from placement.
            migrationBuilder.Sql("UPDATE orders SET payment_due_at = placed_at + interval '30 minutes' WHERE status = 'PendingPayment' AND payment_due_at IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "payment_due_at",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "pays_on_credit",
                table: "orders");
        }
    }
}
