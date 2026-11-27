using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mechware.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffRoleChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "staff_role_changes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    roles_before = table.Column<string[]>(type: "text[]", nullable: false),
                    roles_after = table.Column<string[]>(type: "text[]", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decision_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_role_changes", x => x.id);
                    table.CheckConstraint("ck_staff_role_changes_four_eyes", "status <> 'approved' OR (decided_by IS NOT NULL AND decided_by <> requested_by)");
                    table.CheckConstraint("ck_staff_role_changes_not_self", "requested_by <> user_id AND (decided_by IS NULL OR decided_by <> user_id)");
                    table.ForeignKey(
                        name: "FK_staff_role_changes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_staff_role_changes_status_created",
                table: "staff_role_changes",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_staff_role_changes_open_user",
                table: "staff_role_changes",
                column: "user_id",
                unique: true,
                filter: "status = 'requested'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "staff_role_changes");
        }
    }
}
