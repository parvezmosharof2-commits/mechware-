using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopVerse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompanyMembersUserFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Memberships created while accounts lived in memory may name users that no longer exist. Deleting them
            // silently would hide a company's admins, so stop and make a person decide.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM company_members m LEFT JOIN users u ON u.id = m.user_id WHERE u.id IS NULL) THEN
        RAISE EXCEPTION 'company_members has rows whose user_id is not in users; reconcile them before adding the foreign key';
    END IF;
END $$;");

            migrationBuilder.AddForeignKey(
                name: "FK_company_members_users_user_id",
                table: "company_members",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_company_members_users_user_id",
                table: "company_members");
        }
    }
}
