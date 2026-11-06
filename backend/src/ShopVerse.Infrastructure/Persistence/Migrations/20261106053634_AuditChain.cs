using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopVerse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AuditChainSql.Install);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_audit_logs_append_only ON audit_logs;
                DROP TRIGGER IF EXISTS trg_audit_logs_no_truncate ON audit_logs;
                DROP FUNCTION IF EXISTS verify_audit_chain();
                DROP FUNCTION IF EXISTS seal_audit_logs(integer, interval);
                DROP FUNCTION IF EXISTS audit_logs_append_only();
                DROP FUNCTION IF EXISTS audit_row_hash(text, audit_logs);
                DROP INDEX IF EXISTS ix_audit_logs_unsealed;
                DROP INDEX IF EXISTS ux_audit_logs_chain_position;
                ALTER TABLE audit_logs DROP COLUMN IF EXISTS hash, DROP COLUMN IF EXISTS prev_hash, DROP COLUMN IF EXISTS chain_position;
                """);
        }
    }
}
