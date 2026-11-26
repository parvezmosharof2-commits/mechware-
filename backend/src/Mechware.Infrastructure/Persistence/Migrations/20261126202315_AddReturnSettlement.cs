using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mechware.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReturnSettlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "refund_id",
                table: "return_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "settled_at",
                table: "return_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "settled_by",
                table: "return_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "settlement",
                table: "return_requests",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "settlement_amount_minor",
                table: "return_requests",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "settlement_requested_at",
                table: "return_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "settlement_requested_by",
                table: "return_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_return_requests_settled_by",
                table: "return_requests",
                column: "settled_by");

            migrationBuilder.CreateIndex(
                name: "IX_return_requests_settlement_requested_by",
                table: "return_requests",
                column: "settlement_requested_by");

            migrationBuilder.CreateIndex(
                name: "ux_return_requests_refund",
                table: "return_requests",
                column: "refund_id",
                unique: true,
                filter: "refund_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_return_requests_settled",
                table: "return_requests",
                sql: "(settled_by IS NULL) = (settled_at IS NULL) AND (settled_at IS NULL) = (status NOT IN ('Refunded', 'CreditNoted')) AND (status <> 'CreditNoted' OR settlement IS NOT DISTINCT FROM 'CreditNote') AND (status <> 'Refunded' OR COALESCE(settlement IN ('Refund', 'CreditReduction'), false)) AND (settled_by IS NULL OR settled_by <> settlement_requested_by)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_return_requests_settlement",
                table: "return_requests",
                sql: "(settlement IS NULL) = (settlement_requested_by IS NULL) AND (settlement IS NULL) = (settlement_requested_at IS NULL) AND (settlement IS NULL OR status IN ('Inspected', 'Refunded', 'CreditNoted')) AND (settlement IS NULL OR settlement IN ('CreditNote', 'Refund', 'CreditReduction')) AND (refund_id IS NOT NULL) = (settlement IS NOT DISTINCT FROM 'Refund') AND (settlement_amount_minor IS NOT NULL) = COALESCE(settlement IN ('Refund', 'CreditReduction'), false) AND (settlement_amount_minor IS NULL OR settlement_amount_minor > 0)");

            migrationBuilder.AddForeignKey(
                name: "FK_return_requests_refunds_refund_id",
                table: "return_requests",
                column: "refund_id",
                principalTable: "refunds",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_return_requests_users_settled_by",
                table: "return_requests",
                column: "settled_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_return_requests_users_settlement_requested_by",
                table: "return_requests",
                column: "settlement_requested_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_return_requests_refunds_refund_id",
                table: "return_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_return_requests_users_settled_by",
                table: "return_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_return_requests_users_settlement_requested_by",
                table: "return_requests");

            migrationBuilder.DropIndex(
                name: "IX_return_requests_settled_by",
                table: "return_requests");

            migrationBuilder.DropIndex(
                name: "IX_return_requests_settlement_requested_by",
                table: "return_requests");

            migrationBuilder.DropIndex(
                name: "ux_return_requests_refund",
                table: "return_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_return_requests_settled",
                table: "return_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_return_requests_settlement",
                table: "return_requests");

            migrationBuilder.DropColumn(
                name: "refund_id",
                table: "return_requests");

            migrationBuilder.DropColumn(
                name: "settled_at",
                table: "return_requests");

            migrationBuilder.DropColumn(
                name: "settled_by",
                table: "return_requests");

            migrationBuilder.DropColumn(
                name: "settlement",
                table: "return_requests");

            migrationBuilder.DropColumn(
                name: "settlement_amount_minor",
                table: "return_requests");

            migrationBuilder.DropColumn(
                name: "settlement_requested_at",
                table: "return_requests");

            migrationBuilder.DropColumn(
                name: "settlement_requested_by",
                table: "return_requests");
        }
    }
}
