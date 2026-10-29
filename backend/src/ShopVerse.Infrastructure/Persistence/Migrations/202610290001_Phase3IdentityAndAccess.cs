using Microsoft.EntityFrameworkCore.Migrations;

namespace ShopVerse.Infrastructure.Persistence.Migrations;

/// <summary>
/// Phase 3 EF Core Migration creating Identity, RBAC, Token Family, OTP, AuditLog, and BD Location tables.
/// </summary>
public partial class Phase3IdentityAndAccess : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "users",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                password_hash = table.Column<string>(type: "text", nullable: true),
                full_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                avatar_url = table.Column<string>(type: "text", nullable: true),
                locale = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false, defaultValue: "en"),
                status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                two_factor_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                two_factor_secret = table.Column<string>(type: "text", nullable: true),
                two_factor_recovery_codes_json = table.Column<string>(type: "text", nullable: true),
                last_used_totp_time_step = table.Column<long>(type: "bigint", nullable: true),
                google_subject_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                seller_id = table.Column<Guid>(type: "uuid", nullable: true),
                failed_login_attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                lockout_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                email_verified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                phone_verified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                last_login_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_users", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "addresses",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                label = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                recipient_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                line1 = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                line2 = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                area = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                upazila = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                district = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                division = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                postal_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false, defaultValue: "BD"),
                is_default_shipping = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                is_default_billing = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                latitude = table.Column<decimal>(type: "numeric(10,7)", nullable: true),
                longitude = table.Column<decimal>(type: "numeric(10,7)", nullable: true),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_addresses", x => x.id);
                table.ForeignKey("fk_addresses_users_user_id", x => x.user_id, "users", "id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "roles",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                description = table.Column<string>(type: "text", nullable: true),
                is_system = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_roles", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "permissions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                module = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                description = table.Column<string>(type: "text", nullable: true),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_permissions", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "role_permissions",
            columns: table => new
            {
                role_id = table.Column<Guid>(type: "uuid", nullable: false),
                permission_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_role_permissions", x => new { x.role_id, x.permission_id });
            });

        migrationBuilder.CreateTable(
            name: "user_roles",
            columns: table => new
            {
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                role_id = table.Column<Guid>(type: "uuid", nullable: false),
                role_name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                assigned_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_user_roles", x => new { x.user_id, x.role_id });
            });

        migrationBuilder.CreateTable(
            name: "refresh_tokens",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                token_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                family_id = table.Column<Guid>(type: "uuid", nullable: false),
                is_used = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                is_revoked = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                replaced_by_token_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                created_by_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                user_agent = table.Column<string>(type: "text", nullable: true),
                expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_refresh_tokens", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "otp_codes",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                recipient = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                code_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                reference_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                is_consumed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_otp_codes", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "audit_logs",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                actor_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                entity_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                entity_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                old_values = table.Column<string>(type: "jsonb", nullable: true),
                new_values = table.Column<string>(type: "jsonb", nullable: true),
                correlation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_audit_logs", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "bd_locations",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                name_en = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                name_bn = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                shipping_zone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                postal_code_prefix = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_bd_locations", x => x.id);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "bd_locations");
        migrationBuilder.DropTable(name: "audit_logs");
        migrationBuilder.DropTable(name: "otp_codes");
        migrationBuilder.DropTable(name: "refresh_tokens");
        migrationBuilder.DropTable(name: "user_roles");
        migrationBuilder.DropTable(name: "role_permissions");
        migrationBuilder.DropTable(name: "permissions");
        migrationBuilder.DropTable(name: "roles");
        migrationBuilder.DropTable(name: "addresses");
        migrationBuilder.DropTable(name: "users");
    }
}
