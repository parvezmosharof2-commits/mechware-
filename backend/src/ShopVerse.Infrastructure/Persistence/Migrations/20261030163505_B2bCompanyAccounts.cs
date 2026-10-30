using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopVerse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class B2bCompanyAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "companies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    trade_licence_no = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    bin = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: false),
                    tin = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    industry = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    credit_terms = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    credit_limit = table.Column<long>(type: "bigint", nullable: false),
                    credit_used = table.Column<long>(type: "bigint", nullable: false),
                    default_price_list_id = table.Column<Guid>(type: "uuid", nullable: true),
                    account_manager_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_companies", x => x.id);
                    table.CheckConstraint("ck_companies_credit_non_negative", "credit_limit >= 0 AND credit_used >= 0");
                    table.CheckConstraint("ck_companies_credit_within_limit", "credit_used <= credit_limit");
                    table.CheckConstraint("ck_companies_terms_limit", "credit_terms <> 0 OR credit_limit = 0");
                });

            migrationBuilder.CreateTable(
                name: "company_members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    roles = table.Column<int>(type: "integer", nullable: false),
                    spend_limit = table.Column<long>(type: "bigint", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    cost_centre_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_company_members", x => x.id);
                    table.CheckConstraint("ck_company_members_roles", "roles BETWEEN 1 AND 15");
                    table.CheckConstraint("ck_company_members_spend", "spend_limit IS NULL OR spend_limit >= 0");
                    table.ForeignKey(
                        name: "FK_company_members_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "company_sites",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    contact_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    contact_phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    line1 = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    line2 = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    upazila = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    district = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    division = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    postal_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    is_default_delivery = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_company_sites", x => x.id);
                    table.ForeignKey(
                        name: "FK_company_sites_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "cost_centres",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    monthly_budget = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cost_centres", x => x.id);
                    table.CheckConstraint("ck_cost_centres_budget", "monthly_budget IS NULL OR monthly_budget >= 0");
                    table.ForeignKey(
                        name: "FK_cost_centres_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_companies_status",
                table: "companies",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_companies_bin",
                table: "companies",
                column: "bin",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_company_members_user",
                table: "company_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_company_members_company_user",
                table: "company_members",
                columns: new[] { "company_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_company_sites_one_default",
                table: "company_sites",
                column: "company_id",
                unique: true,
                filter: "is_default_delivery");

            migrationBuilder.CreateIndex(
                name: "ux_cost_centres_company_code",
                table: "cost_centres",
                columns: new[] { "company_id", "code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "company_members");

            migrationBuilder.DropTable(
                name: "company_sites");

            migrationBuilder.DropTable(
                name: "cost_centres");

            migrationBuilder.DropTable(
                name: "companies");
        }
    }
}
