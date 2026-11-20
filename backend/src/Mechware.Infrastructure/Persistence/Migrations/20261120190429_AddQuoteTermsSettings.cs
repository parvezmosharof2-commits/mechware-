using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mechware.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuoteTermsSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "terms_snapshot",
                table: "quote_versions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "quote_terms_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    terms_en = table.Column<string[]>(type: "text[]", nullable: false),
                    terms_bn = table.Column<string[]>(type: "text[]", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quote_terms_settings", x => x.id);
                    table.CheckConstraint("ck_quote_terms_settings_lines", "cardinality(terms_en) <= 12 AND cardinality(terms_bn) <= 12");
                    table.CheckConstraint("ck_quote_terms_settings_single", "id = '00000000-0000-0000-0000-0000000d4200'");
                    table.ForeignKey(
                        name: "FK_quote_terms_settings_users_updated_by",
                        column: x => x.updated_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_quote_terms_settings_updated_by",
                table: "quote_terms_settings",
                column: "updated_by");

            // The guard of a quote version now lets 'terms_snapshot' be written when the version leaves Draft (and never after).
            migrationBuilder.Sql(QuoteSql.Install);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The guard keeps 'terms_snapshot' in its list: a key the table no longer has is harmless.
            migrationBuilder.DropTable(
                name: "quote_terms_settings");

            migrationBuilder.DropColumn(
                name: "terms_snapshot",
                table: "quote_versions");
        }
    }
}
