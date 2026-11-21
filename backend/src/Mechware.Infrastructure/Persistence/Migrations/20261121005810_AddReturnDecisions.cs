using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mechware.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReturnDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_return_requests_status",
                table: "return_requests");

            migrationBuilder.AddColumn<Guid>(
                name: "order_id",
                table: "stock_movements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "decided_at",
                table: "return_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "decided_by",
                table: "return_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "inspected_at",
                table: "return_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "inspected_by",
                table: "return_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "received_at",
                table: "return_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "received_by",
                table: "return_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rejection_reason",
                table: "return_requests",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "return_requests",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<int>(
                name: "accepted_quantity",
                table: "return_request_lines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "inspection_note",
                table: "return_request_lines",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "restock_quantity",
                table: "return_request_lines",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_order",
                table: "stock_movements",
                column: "order_id",
                filter: "order_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_return_requests_decided_by",
                table: "return_requests",
                column: "decided_by");

            migrationBuilder.CreateIndex(
                name: "IX_return_requests_inspected_by",
                table: "return_requests",
                column: "inspected_by");

            migrationBuilder.CreateIndex(
                name: "IX_return_requests_received_by",
                table: "return_requests",
                column: "received_by");

            migrationBuilder.AddCheckConstraint(
                name: "ck_return_requests_decided",
                table: "return_requests",
                sql: "(decided_by IS NULL) = (decided_at IS NULL) AND (decided_at IS NULL) = (status IN ('Requested', 'Withdrawn'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_return_requests_inspected",
                table: "return_requests",
                sql: "(inspected_by IS NULL) = (inspected_at IS NULL) AND (inspected_at IS NULL) = (status NOT IN ('Inspected', 'Refunded', 'CreditNoted'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_return_requests_received",
                table: "return_requests",
                sql: "(received_by IS NULL) = (received_at IS NULL) AND (received_at IS NULL) = (status IN ('Requested', 'Withdrawn', 'Approved', 'Rejected'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_return_requests_rejection",
                table: "return_requests",
                sql: "(status = 'Rejected') = (rejection_reason IS NOT NULL) AND (rejection_reason IS NULL OR (char_length(rejection_reason) BETWEEN 5 AND 500 AND rejection_reason = btrim(rejection_reason)))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_return_requests_status",
                table: "return_requests",
                sql: "status IN ('Requested', 'Approved', 'Rejected', 'Received', 'Inspected', 'Refunded', 'CreditNoted', 'Withdrawn')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_return_request_lines_inspection",
                table: "return_request_lines",
                sql: "(accepted_quantity IS NULL AND restock_quantity IS NULL AND inspection_note IS NULL) OR (accepted_quantity BETWEEN 0 AND quantity AND restock_quantity BETWEEN 0 AND accepted_quantity AND (inspection_note IS NULL OR (char_length(inspection_note) BETWEEN 1 AND 500 AND inspection_note = btrim(inspection_note))))");

            migrationBuilder.AddForeignKey(
                name: "FK_return_requests_users_decided_by",
                table: "return_requests",
                column: "decided_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_return_requests_users_inspected_by",
                table: "return_requests",
                column: "inspected_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_return_requests_users_received_by",
                table: "return_requests",
                column: "received_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // F2.3a guards: line immutability + inspect-once, the status graph, the deferred inspected-lines check.
            migrationBuilder.Sql(ReturnSql.Install);
        }

        /// <summary>The F2.1 guards exactly as AddReturnRequests installed them (frozen here: ReturnSql.Install has moved on).</summary>
        private const string ReturnGuardsF21 = """
            CREATE OR REPLACE FUNCTION return_request_lines_guard() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            DECLARE
                request_order uuid;
                item_order uuid;
                ordered int;
                claimed int;
            BEGIN
                SELECT r.order_id INTO request_order FROM return_requests r WHERE r.id = NEW.return_request_id;
                SELECT s.order_id, i.quantity INTO item_order, ordered
                  FROM order_items i JOIN sub_orders s ON s.id = i.sub_order_id
                 WHERE i.id = NEW.order_item_id FOR UPDATE OF i;
                IF item_order IS DISTINCT FROM request_order THEN
                    RAISE EXCEPTION 'return line % is not a line of the returned order', NEW.order_item_id USING ERRCODE = 'check_violation';
                END IF;
                SELECT COALESCE(SUM(l.quantity), 0) INTO claimed
                  FROM return_request_lines l JOIN return_requests r ON r.id = l.return_request_id
                 WHERE l.order_item_id = NEW.order_item_id AND l.id <> NEW.id AND r.status NOT IN ('Withdrawn', 'Rejected');
                IF claimed + NEW.quantity > ordered THEN
                    RAISE EXCEPTION 'return of order line % exceeds the delivered quantity', NEW.order_item_id USING ERRCODE = 'check_violation';
                END IF;
                RETURN NEW;
            END
            $fn$;

            DROP TRIGGER IF EXISTS trg_return_request_lines_guard ON return_request_lines;
            CREATE TRIGGER trg_return_request_lines_guard BEFORE INSERT OR UPDATE ON return_request_lines
                FOR EACH ROW EXECUTE FUNCTION return_request_lines_guard();

            CREATE OR REPLACE FUNCTION return_requests_no_reopen() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
                IF OLD.status IN ('Withdrawn', 'Rejected') AND NEW.status <> OLD.status THEN
                    RAISE EXCEPTION 'a % return request cannot change status', OLD.status USING ERRCODE = 'check_violation';
                END IF;
                RETURN NEW;
            END
            $fn$;

            DROP TRIGGER IF EXISTS trg_return_requests_no_reopen ON return_requests;
            CREATE TRIGGER trg_return_requests_no_reopen BEFORE UPDATE OF status ON return_requests
                FOR EACH ROW EXECUTE FUNCTION return_requests_no_reopen();
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ReturnSql.Uninstall);

            migrationBuilder.DropForeignKey(
                name: "FK_return_requests_users_decided_by",
                table: "return_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_return_requests_users_inspected_by",
                table: "return_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_return_requests_users_received_by",
                table: "return_requests");

            migrationBuilder.DropIndex(
                name: "ix_stock_movements_order",
                table: "stock_movements");

            migrationBuilder.DropIndex(
                name: "IX_return_requests_decided_by",
                table: "return_requests");

            migrationBuilder.DropIndex(
                name: "IX_return_requests_inspected_by",
                table: "return_requests");

            migrationBuilder.DropIndex(
                name: "IX_return_requests_received_by",
                table: "return_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_return_requests_decided",
                table: "return_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_return_requests_inspected",
                table: "return_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_return_requests_received",
                table: "return_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_return_requests_rejection",
                table: "return_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_return_requests_status",
                table: "return_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_return_request_lines_inspection",
                table: "return_request_lines");

            migrationBuilder.DropColumn(
                name: "order_id",
                table: "stock_movements");

            migrationBuilder.DropColumn(
                name: "decided_at",
                table: "return_requests");

            migrationBuilder.DropColumn(
                name: "decided_by",
                table: "return_requests");

            migrationBuilder.DropColumn(
                name: "inspected_at",
                table: "return_requests");

            migrationBuilder.DropColumn(
                name: "inspected_by",
                table: "return_requests");

            migrationBuilder.DropColumn(
                name: "received_at",
                table: "return_requests");

            migrationBuilder.DropColumn(
                name: "received_by",
                table: "return_requests");

            migrationBuilder.DropColumn(
                name: "rejection_reason",
                table: "return_requests");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "return_requests");

            migrationBuilder.DropColumn(
                name: "accepted_quantity",
                table: "return_request_lines");

            migrationBuilder.DropColumn(
                name: "inspection_note",
                table: "return_request_lines");

            migrationBuilder.DropColumn(
                name: "restock_quantity",
                table: "return_request_lines");

            migrationBuilder.AddCheckConstraint(
                name: "ck_return_requests_status",
                table: "return_requests",
                sql: "status IN ('Requested', 'Approved', 'Rejected', 'Received', 'Refunded', 'CreditNoted', 'Withdrawn')");

            migrationBuilder.Sql(ReturnGuardsF21);
        }
    }
}
