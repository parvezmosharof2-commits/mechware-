using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mechware.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InAppNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "in_app_notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    title_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    @params = table.Column<string>(name: "params", type: "jsonb", nullable: false),
                    link_path = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_in_app_notifications", x => x.id);
                    table.CheckConstraint("ck_in_app_notifications_link_relative", "link_path LIKE '/%' AND link_path NOT LIKE '//%' AND position(chr(92) in link_path) = 0 AND link_path !~ '[[:cntrl:]]'");
                    table.ForeignKey(
                        name: "FK_in_app_notifications_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_in_app_notifications_user_read_created",
                table: "in_app_notifications",
                columns: new[] { "user_id", "read_at", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_in_app_notifications_event_user",
                table: "in_app_notifications",
                columns: new[] { "event_id", "user_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "in_app_notifications");
        }
    }
}
