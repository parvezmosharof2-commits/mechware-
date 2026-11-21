using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mechware.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReturnRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "return_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    withdrawn_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_return_requests", x => x.id);
                    table.CheckConstraint("ck_return_requests_key", "idempotency_key ~ '^[A-Za-z0-9_-]{8,100}$' AND char_length(request_hash) = 64");
                    table.CheckConstraint("ck_return_requests_note", "note IS NULL OR (char_length(note) BETWEEN 1 AND 1000 AND note = btrim(note))");
                    table.CheckConstraint("ck_return_requests_reason", "reason IN ('Damaged', 'WrongItem', 'NotAsDescribed', 'Other') AND (reason <> 'Other' OR note IS NOT NULL)");
                    table.CheckConstraint("ck_return_requests_status", "status IN ('Requested', 'Approved', 'Rejected', 'Received', 'Refunded', 'CreditNoted', 'Withdrawn')");
                    table.CheckConstraint("ck_return_requests_withdrawn", "(status = 'Withdrawn') = (withdrawn_at IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_return_requests_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_return_requests_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_return_requests_users_requested_by",
                        column: x => x.requested_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "return_request_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    return_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_return_request_lines", x => x.id);
                    table.CheckConstraint("ck_return_request_lines_qty", "quantity BETWEEN 1 AND 10000");
                    table.ForeignKey(
                        name: "FK_return_request_lines_order_items_order_item_id",
                        column: x => x.order_item_id,
                        principalTable: "order_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_return_request_lines_return_requests_return_request_id",
                        column: x => x.return_request_id,
                        principalTable: "return_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_return_request_lines_order_item",
                table: "return_request_lines",
                column: "order_item_id");

            migrationBuilder.CreateIndex(
                name: "ux_return_request_lines_item",
                table: "return_request_lines",
                columns: new[] { "return_request_id", "order_item_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_return_requests_company_id",
                table: "return_requests",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "ix_return_requests_order",
                table: "return_requests",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_return_requests_requester",
                table: "return_requests",
                columns: new[] { "requested_by", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_return_requests_status",
                table: "return_requests",
                columns: new[] { "status", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ux_return_requests_idempotency",
                table: "return_requests",
                columns: new[] { "requested_by", "idempotency_key" },
                unique: true);

            // F2.1: the quantity guard and the no-reopen rule (idempotent SQL, also run by test fixtures after EnsureCreated).
            migrationBuilder.Sql(ReturnSql.Install);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ReturnSql.Uninstall);

            migrationBuilder.DropTable(
                name: "return_request_lines");

            migrationBuilder.DropTable(
                name: "return_requests");
        }
    }
}
