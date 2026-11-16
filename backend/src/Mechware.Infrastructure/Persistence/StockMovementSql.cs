namespace Mechware.Infrastructure.Persistence;

/// <summary>
/// Makes <c>stock_movements</c> append-only, like <c>audit_logs</c> (E7.1): a restock that already happened can neither be
/// rewritten nor removed, so the ledger always explains the on-hand balance. Idempotent: the migration runs it, and tests
/// that build their schema with EnsureCreated run it too.
/// </summary>
public static class StockMovementSql
{
    public const string Install = """
        CREATE OR REPLACE FUNCTION stock_movements_append_only() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        BEGIN
            RAISE EXCEPTION 'stock_movements is append-only' USING ERRCODE = 'insufficient_privilege';
        END
        $fn$;

        DROP TRIGGER IF EXISTS trg_stock_movements_append_only ON stock_movements;
        CREATE TRIGGER trg_stock_movements_append_only BEFORE UPDATE OR DELETE ON stock_movements
            FOR EACH ROW EXECUTE FUNCTION stock_movements_append_only();
        DROP TRIGGER IF EXISTS trg_stock_movements_no_truncate ON stock_movements;
        CREATE TRIGGER trg_stock_movements_no_truncate BEFORE TRUNCATE ON stock_movements
            FOR EACH STATEMENT EXECUTE FUNCTION stock_movements_append_only();
        """;
}
