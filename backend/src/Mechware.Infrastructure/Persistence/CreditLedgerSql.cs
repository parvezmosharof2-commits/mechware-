namespace Mechware.Infrastructure.Persistence;

/// <summary>
/// Database guards of the credit ledger (ADR-0010 §2.3, threat model §১২ CS13/CS14), so a code bug cannot make
/// <c>companies.credit_used</c> unexplainable:
/// <list type="bullet">
/// <item><c>credit_ledger_entries</c> is append-only: UPDATE, DELETE and TRUNCATE raise <c>insufficient_privilege</c>.</item>
/// <item>A deferred constraint trigger checks at COMMIT that every new line continues its company's chain (line n − 1
/// exists and each running total = the previous one + the delta), and that the company's cached
/// <c>credit_used / credit_on_account / credit_ledger_seq</c> equal its last line (0/0/0 without one). It runs for new lines
/// and for any change of those company columns, so a path that moves <c>credit_used</c> without a line, or writes a line
/// without moving the company, fails at commit. Deferred (not BEFORE INSERT), because EF Core orders the inserts of one save
/// by key, not by <c>seq</c>; each check is two index lookups.</item>
/// </list>
/// The signs per kind are CHECKs in the EF model. Idempotent: the migration runs it, and tests that build their schema with
/// EnsureCreated run it too. <see cref="Backfill"/> is the one-time charge of the credit orders confirmed before the ledger.
/// </summary>
public static class CreditLedgerSql
{
    public const string Install = """
        CREATE OR REPLACE FUNCTION credit_ledger_entries_append_only() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        BEGIN
            RAISE EXCEPTION 'credit_ledger_entries is append-only' USING ERRCODE = 'insufficient_privilege';
        END
        $fn$;

        DROP TRIGGER IF EXISTS trg_credit_ledger_entries_append_only ON credit_ledger_entries;
        CREATE TRIGGER trg_credit_ledger_entries_append_only BEFORE UPDATE OR DELETE ON credit_ledger_entries
            FOR EACH ROW EXECUTE FUNCTION credit_ledger_entries_append_only();
        DROP TRIGGER IF EXISTS trg_credit_ledger_entries_no_truncate ON credit_ledger_entries;
        CREATE TRIGGER trg_credit_ledger_entries_no_truncate BEFORE TRUNCATE ON credit_ledger_entries
            FOR EACH STATEMENT EXECUTE FUNCTION credit_ledger_entries_append_only();

        CREATE OR REPLACE FUNCTION credit_ledger_check() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        DECLARE
            company uuid;
            prev_r bigint := 0;
            prev_a bigint := 0;
            used bigint;
            on_account bigint;
            lines bigint;
            tip_r bigint := 0;
            tip_a bigint := 0;
            tip_seq bigint := 0;
        BEGIN
            IF TG_TABLE_NAME = 'companies' THEN
                company := NEW.id;
            ELSE
                company := NEW.company_id;
                IF NEW.seq > 1 THEN
                    SELECT receivable_after_minor, on_account_after_minor INTO prev_r, prev_a
                      FROM credit_ledger_entries WHERE company_id = company AND seq = NEW.seq - 1;
                    IF NOT FOUND THEN
                        prev_r := NULL;
                    END IF;
                END IF;
                IF prev_r IS NULL OR NEW.receivable_after_minor <> prev_r + NEW.receivable_delta_minor
                   OR NEW.on_account_after_minor <> prev_a + NEW.on_account_delta_minor THEN
                    RAISE EXCEPTION 'credit_ledger_entries: line % of company % does not continue the chain (gap or wrong running total)',
                        NEW.seq, company USING ERRCODE = 'integrity_constraint_violation';
                END IF;
            END IF;

            SELECT credit_used, credit_on_account, credit_ledger_seq INTO used, on_account, lines FROM companies WHERE id = company;
            IF NOT FOUND THEN
                RETURN NULL;
            END IF;
            SELECT receivable_after_minor, on_account_after_minor, seq INTO tip_r, tip_a, tip_seq
              FROM credit_ledger_entries WHERE company_id = company ORDER BY seq DESC LIMIT 1;
            IF NOT FOUND THEN
                tip_r := 0; tip_a := 0; tip_seq := 0;
            END IF;
            IF (used, on_account, lines) IS DISTINCT FROM (tip_r, tip_a, tip_seq) THEN
                RAISE EXCEPTION 'credit_ledger_entries: company % has credit used %, on account %, line % but its ledger says %, %, %',
                    company, used, on_account, lines, tip_r, tip_a, tip_seq USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            RETURN NULL;
        END
        $fn$;

        DROP TRIGGER IF EXISTS trg_credit_ledger_entries_check ON credit_ledger_entries;
        CREATE CONSTRAINT TRIGGER trg_credit_ledger_entries_check AFTER INSERT ON credit_ledger_entries
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION credit_ledger_check();
        DROP TRIGGER IF EXISTS trg_companies_credit_check ON companies;
        CREATE CONSTRAINT TRIGGER trg_companies_credit_check AFTER INSERT OR UPDATE OF credit_used, credit_on_account, credit_ledger_seq ON companies
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION credit_ledger_check();
        """;

    /// <summary>
    /// B3.3b, threat model CS2/CS20 backstop: <c>credit_payments</c> and <c>credit_adjustments</c> are created in their first
    /// status and change only by their next step (recorded → verified | rejected, verified → bounced; requested → approved |
    /// rejected), each step writing only its own columns; anything else, and every DELETE or TRUNCATE, is refused. So a
    /// verified payment's amount, method, reference or allocations can never be edited. Idempotent, like <see cref="Install"/>.
    /// </summary>
    public const string InstallSettlement = """
        CREATE OR REPLACE FUNCTION credit_settlement_guard() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        DECLARE
            first_status text := CASE TG_TABLE_NAME WHEN 'credit_payments' THEN 'recorded' ELSE 'requested' END;
            allowed text[];
        BEGIN
            IF TG_OP = 'INSERT' THEN
                IF NEW.status <> first_status THEN
                    RAISE EXCEPTION '%: a row is created as %', TG_TABLE_NAME, first_status USING ERRCODE = 'integrity_constraint_violation';
                END IF;
                RETURN NEW;
            END IF;
            IF TG_OP <> 'UPDATE' THEN
                RAISE EXCEPTION '%: money records are never deleted', TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
            END IF;
            allowed := CASE
                WHEN NEW.status = OLD.status THEN ARRAY['updated_at']
                WHEN OLD.status = 'recorded' AND NEW.status = 'verified' THEN ARRAY['updated_at', 'status', 'verified_by', 'verified_at', 'cleared_on']
                WHEN OLD.status IN ('recorded', 'requested') AND NEW.status = 'rejected'
                    THEN ARRAY['updated_at', 'status', 'rejected_by', 'rejected_at', 'decision_reason']
                WHEN OLD.status = 'verified' AND NEW.status = 'bounced' THEN ARRAY['updated_at', 'status', 'bounced_by', 'bounced_at', 'decision_reason']
                WHEN OLD.status = 'requested' AND NEW.status = 'approved' THEN ARRAY['updated_at', 'status', 'approved_by', 'approved_at']
            END;
            IF allowed IS NULL THEN
                RAISE EXCEPTION '%: % cannot become %', TG_TABLE_NAME, OLD.status, NEW.status USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF (to_jsonb(NEW) - allowed) IS DISTINCT FROM (to_jsonb(OLD) - allowed) THEN
                RAISE EXCEPTION '%: a % row changes only by its next step', TG_TABLE_NAME, OLD.status USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;

        DROP TRIGGER IF EXISTS trg_credit_payments_guard ON credit_payments;
        CREATE TRIGGER trg_credit_payments_guard BEFORE INSERT OR UPDATE OR DELETE ON credit_payments
            FOR EACH ROW EXECUTE FUNCTION credit_settlement_guard();
        DROP TRIGGER IF EXISTS trg_credit_payments_no_truncate ON credit_payments;
        CREATE TRIGGER trg_credit_payments_no_truncate BEFORE TRUNCATE ON credit_payments
            FOR EACH STATEMENT EXECUTE FUNCTION credit_settlement_guard();
        DROP TRIGGER IF EXISTS trg_credit_adjustments_guard ON credit_adjustments;
        CREATE TRIGGER trg_credit_adjustments_guard BEFORE INSERT OR UPDATE OR DELETE ON credit_adjustments
            FOR EACH ROW EXECUTE FUNCTION credit_settlement_guard();
        DROP TRIGGER IF EXISTS trg_credit_adjustments_no_truncate ON credit_adjustments;
        CREATE TRIGGER trg_credit_adjustments_no_truncate BEFORE TRUNCATE ON credit_adjustments
            FOR EACH STATEMENT EXECUTE FUNCTION credit_settlement_guard();
        """;

    /// <summary>
    /// ADR-0010 §3: one <c>charge</c> per credit order confirmed before the ledger existed (actor system, due = confirmation
    /// Dhaka date + the company's terms days), for companies that have no line yet. A company whose <c>credit_used</c> is not
    /// the sum of those orders stops it with the company ids: it never invents an opening balance. Runs the deferred checks
    /// at its end, so later DDL in the same transaction does not meet pending trigger events. Safe to run again.
    /// </summary>
    public const string Backfill = """
        DO $backfill$
        DECLARE
            bad text;
            entry record;
        BEGIN
            DROP TABLE IF EXISTS credit_backfill;
            CREATE TEMP TABLE credit_backfill AS
            SELECT o.id AS order_id, o.company_id, o.grand_total,
                   coalesce(o.confirmed_at,
                            (SELECT min(e.at) FROM order_events e WHERE e.order_id = o.id AND e.to_status = 'Confirmed'),
                            o.placed_at) AS confirmed_at
              FROM orders o
             WHERE o.pays_on_credit AND o.company_id IS NOT NULL AND o.grand_total > 0
               AND (o.confirmed_at IS NOT NULL OR EXISTS (SELECT 1 FROM order_events e WHERE e.order_id = o.id AND e.to_status = 'Confirmed'))
               AND NOT EXISTS (SELECT 1 FROM credit_ledger_entries l WHERE l.company_id = o.company_id);

            SELECT string_agg(c.id::text, ', ' ORDER BY c.id) INTO bad
              FROM companies c
              LEFT JOIN (SELECT company_id, sum(grand_total) AS total FROM credit_backfill GROUP BY company_id) b ON b.company_id = c.id
             WHERE c.credit_ledger_seq = 0 AND c.credit_used <> coalesce(b.total, 0);
            IF bad IS NOT NULL THEN
                RAISE EXCEPTION 'credit ledger backfill stopped: credit_used is not the sum of the confirmed credit orders for companies %', bad
                    USING ERRCODE = 'integrity_constraint_violation';
            END IF;

            FOR entry IN
                SELECT b.*, c.credit_terms,
                       row_number() OVER (PARTITION BY b.company_id ORDER BY b.confirmed_at, b.order_id) AS seq,
                       sum(b.grand_total) OVER (PARTITION BY b.company_id ORDER BY b.confirmed_at, b.order_id) AS running
                  FROM credit_backfill b JOIN companies c ON c.id = b.company_id
                 ORDER BY b.company_id, seq
            LOOP
                INSERT INTO credit_ledger_entries (id, company_id, seq, event_id, kind, order_id, receivable_delta_minor,
                    on_account_delta_minor, receivable_after_minor, on_account_after_minor, due_on, occurred_on, currency,
                    idempotency_key, reason, created_at)
                VALUES (gen_random_uuid(), entry.company_id, entry.seq, gen_random_uuid(), 'charge', entry.order_id, entry.grand_total, 0,
                    entry.running, 0,
                    ((entry.confirmed_at AT TIME ZONE 'UTC') + interval '6 hours')::date + entry.credit_terms,
                    ((entry.confirmed_at AT TIME ZONE 'UTC') + interval '6 hours')::date, 'BDT',
                    'charge:' || replace(entry.order_id::text, '-', ''), 'Backfill: confirmed before the credit ledger (B3.3)', now());
            END LOOP;

            UPDATE companies c SET credit_ledger_seq = n.lines
              FROM (SELECT company_id, count(*) AS lines FROM credit_backfill GROUP BY company_id) n
             WHERE n.company_id = c.id AND c.credit_ledger_seq = 0;
            DROP TABLE credit_backfill;
        END
        $backfill$;
        SET CONSTRAINTS ALL IMMEDIATE;
        """;
}
