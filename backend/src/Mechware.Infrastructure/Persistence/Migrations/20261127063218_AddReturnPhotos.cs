using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mechware.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReturnPhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "return_photos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    return_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slot = table.Column<int>(type: "integer", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_return_photos", x => x.id);
                    table.CheckConstraint("ck_return_photos_file", "size_bytes > 0 AND width > 0 AND height > 0 AND sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_return_photos_key", "storage_key ~ '^uploads/rma/[0-9a-f]{32}/[0-9a-f]{32}[.]jpg$' AND storage_key LIKE ('uploads/rma/' || replace(return_request_id::text, '-', '') || '/%')");
                    table.CheckConstraint("ck_return_photos_slot", "slot BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "FK_return_photos_return_requests_return_request_id",
                        column: x => x.return_request_id,
                        principalTable: "return_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_return_photos_users_uploaded_by",
                        column: x => x.uploaded_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_return_photos_uploaded_by",
                table: "return_photos",
                column: "uploaded_by");

            migrationBuilder.CreateIndex(
                name: "ux_return_photos_key",
                table: "return_photos",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_return_photos_slot",
                table: "return_photos",
                columns: new[] { "return_request_id", "slot" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "return_photos");
        }
    }
}
