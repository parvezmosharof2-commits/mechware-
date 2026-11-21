namespace Mechware.Infrastructure.Persistence;

/// <summary>
/// Database backstop of the gap-free numbering (ADR-0012 §4 backstops; threat model §১৪ IT1, IT10, IT11): a code bug or a
/// hand-written query must not be enough to skip, reuse or rewind a serial.
/// <list type="bullet">
/// <item>A series row is created at fiscal year 0, next serial 1, never issued (the CHECK repeats it).</item>
/// <item>An UPDATE either steps one serial in the same fiscal year, or rolls over to a later year with serial 1 taken
/// (next = 2). Seller and series never change. The issue instant never goes back, and the fiscal year is the July–June year
/// of its Dhaka date (+06:00), so a year computed from the UTC date fails here too.</item>
/// <item>No DELETE, no TRUNCATE.</item>
/// </list>
/// The increment is an ordinary row update, so a ROLLBACK (or a lost connection) undoes it with the issue. The deferred
/// "every counter change has its invoice" check comes with the invoices table (E6.3b). Errors use SQLSTATE 23000 with a
/// message starting with the table name. Idempotent: the migration runs it, and tests that build their schema with
/// EnsureCreated run it too.
/// </summary>
public static class InvoiceNumberingSql
{
    public const string Install = """
        CREATE OR REPLACE FUNCTION invoice_number_counters_guard() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        DECLARE
            dhaka timestamp;
            fy int;
        BEGIN
            IF TG_OP = 'TRUNCATE' THEN
                RAISE EXCEPTION 'invoice_number_counters: counters are never truncated' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'invoice_number_counters: counters are never deleted' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF TG_OP = 'INSERT' THEN
                IF NEW.fiscal_year <> 0 OR NEW.next_serial <> 1 OR NEW.last_issued_at IS NOT NULL THEN
                    RAISE EXCEPTION 'invoice_number_counters: a series starts at fiscal year 0, next serial 1' USING ERRCODE = 'integrity_constraint_violation';
                END IF;
                RETURN NEW;
            END IF;
            IF (NEW.seller_id, NEW.series) IS DISTINCT FROM (OLD.seller_id, OLD.series) THEN
                RAISE EXCEPTION 'invoice_number_counters: seller and series never change' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF NOT ((NEW.fiscal_year = OLD.fiscal_year AND NEW.next_serial = OLD.next_serial + 1)
                    OR (NEW.fiscal_year > OLD.fiscal_year AND NEW.next_serial = 2)) THEN
                RAISE EXCEPTION 'invoice_number_counters: % % steps by one serial or rolls over to serial 1 of a later year (was % next %, got % next %)',
                    OLD.seller_id, OLD.series, OLD.fiscal_year, OLD.next_serial, NEW.fiscal_year, NEW.next_serial
                    USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF NEW.last_issued_at IS NULL OR NEW.last_issued_at < OLD.last_issued_at THEN
                RAISE EXCEPTION 'invoice_number_counters: the issue instant never goes back' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            dhaka := (NEW.last_issued_at AT TIME ZONE 'UTC') + interval '6 hours';
            fy := CASE WHEN extract(month FROM dhaka) >= 7 THEN extract(year FROM dhaka)::int ELSE extract(year FROM dhaka)::int - 1 END;
            IF NEW.fiscal_year <> fy THEN
                RAISE EXCEPTION 'invoice_number_counters: fiscal year % is not the July-June year % of the Dhaka date of the issue', NEW.fiscal_year, fy
                    USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;

        DROP TRIGGER IF EXISTS trg_invoice_number_counters_guard ON invoice_number_counters;
        CREATE TRIGGER trg_invoice_number_counters_guard BEFORE INSERT OR UPDATE OR DELETE ON invoice_number_counters
            FOR EACH ROW EXECUTE FUNCTION invoice_number_counters_guard();
        DROP TRIGGER IF EXISTS trg_invoice_number_counters_no_truncate ON invoice_number_counters;
        CREATE TRIGGER trg_invoice_number_counters_no_truncate BEFORE TRUNCATE ON invoice_number_counters
            FOR EACH STATEMENT EXECUTE FUNCTION invoice_number_counters_guard();
        """;

    public const string Uninstall = """
        DROP TRIGGER IF EXISTS trg_invoice_number_counters_no_truncate ON invoice_number_counters;
        DROP TRIGGER IF EXISTS trg_invoice_number_counters_guard ON invoice_number_counters;
        DROP FUNCTION IF EXISTS invoice_number_counters_guard();
        """;
}
