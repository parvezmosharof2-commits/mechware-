namespace Mechware.Infrastructure.Persistence;

/// <summary>
/// Database half of rule I2 and the numbering backstop (ADR-0012 §4, §8, §16; threat model §১৪ IT1, IT2, IT9):
/// <list type="bullet">
/// <item><c>invoices</c>: INSERT only; the one UPDATE allowed writes <c>pdf_{en,bn}_{key,sha256}</c> from null to a value once;
/// DELETE and TRUNCATE always refused. An insert must name the order's own sub-order of that seller, its shipment, its company
/// and its number; a credit note names a tax invoice of the same seller, order and mode.</item>
/// <item><c>invoice_lines</c>: INSERT only; no UPDATE, DELETE or TRUNCATE.</item>
/// <item>At COMMIT (deferred constraint triggers): every counter step has exactly its invoice (serial <c>next − 1</c>, same year,
/// issued at the counter's instant) and nothing above it; every invoice sits below its series' counter in that year; an
/// invoice has lines 1…n whose sums are its totals (an added line changes them, I4).</item>
/// </list>
/// Errors use SQLSTATE 23000 with a message starting with the table name. Idempotent: the migration runs it, and tests that build
/// their schema with EnsureCreated run it after <see cref="InvoiceNumberingSql"/>.
/// </summary>
public static class InvoiceSql
{
    public const string Install = """
        CREATE OR REPLACE FUNCTION invoices_guard() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        DECLARE
            pdf text[] := ARRAY['pdf_en_key', 'pdf_en_sha256', 'pdf_bn_key', 'pdf_bn_sha256'];
        BEGIN
            IF TG_OP IN ('TRUNCATE', 'DELETE') THEN
                RAISE EXCEPTION '%: issued invoices are never deleted; a wrong one is answered by a credit note', TG_TABLE_NAME
                    USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF TG_OP = 'INSERT' THEN
                IF NEW.kind = 'TaxInvoice' AND NOT EXISTS (
                    SELECT 1 FROM orders o
                      JOIN sub_orders s ON s.id = NEW.sub_order_id AND s.order_id = o.id AND s.seller_id = NEW.seller_id
                      JOIN shipments sh ON sh.id = NEW.shipment_id AND sh.order_id = o.id
                     WHERE o.id = NEW.order_id AND o.order_number = NEW.order_number AND o.company_id IS NOT DISTINCT FROM NEW.company_id) THEN
                    RAISE EXCEPTION 'invoices: a tax invoice names the order''s own sub-order of its seller, its shipment, company and number'
                        USING ERRCODE = 'integrity_constraint_violation';
                END IF;
                IF NEW.kind = 'CreditNote' AND NOT EXISTS (
                    SELECT 1 FROM invoices i WHERE i.id = NEW.original_invoice_id AND i.kind = 'TaxInvoice' AND i.mode = NEW.mode
                       AND i.seller_id = NEW.seller_id AND i.order_id = NEW.order_id AND i.sub_order_id = NEW.sub_order_id) THEN
                    RAISE EXCEPTION 'invoices: a credit note names a tax invoice of the same seller, order and mode'
                        USING ERRCODE = 'integrity_constraint_violation';
                END IF;
                RETURN NEW;
            END IF;
            IF (to_jsonb(NEW) - pdf) IS DISTINCT FROM (to_jsonb(OLD) - pdf) THEN
                RAISE EXCEPTION 'invoices: % is issued; it never changes (corrections by credit note only)', OLD.number
                    USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF (OLD.pdf_en_key IS NOT NULL AND (NEW.pdf_en_key, NEW.pdf_en_sha256) IS DISTINCT FROM (OLD.pdf_en_key, OLD.pdf_en_sha256))
               OR (OLD.pdf_bn_key IS NOT NULL AND (NEW.pdf_bn_key, NEW.pdf_bn_sha256) IS DISTINCT FROM (OLD.pdf_bn_key, OLD.pdf_bn_sha256)) THEN
                RAISE EXCEPTION 'invoices: a stored PDF of % is recorded once', OLD.number USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE OR REPLACE FUNCTION invoice_lines_guard() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        BEGIN
            RAISE EXCEPTION 'invoice_lines: lines of an issued invoice are never changed or deleted' USING ERRCODE = 'integrity_constraint_violation';
        END
        $fn$;

        -- At COMMIT, per counter step: its invoice exists (serial next - 1, same year, the counter's instant), nothing is above it.
        CREATE OR REPLACE FUNCTION invoice_counter_step_check() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        DECLARE
            cur invoice_number_counters%ROWTYPE;
        BEGIN
            IF (SELECT count(*) FROM invoices i WHERE i.seller_id = NEW.seller_id AND i.series = NEW.series AND i.fiscal_year = NEW.fiscal_year
                   AND i.serial = NEW.next_serial - 1 AND i.issued_at = NEW.last_issued_at) <> 1 THEN
                RAISE EXCEPTION 'invoice_number_counters: % % serial % of %: a taken number needs exactly its invoice at commit',
                    NEW.seller_id, NEW.series, NEW.next_serial - 1, NEW.fiscal_year USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            SELECT * INTO cur FROM invoice_number_counters c WHERE c.seller_id = NEW.seller_id AND c.series = NEW.series;
            IF EXISTS (SELECT 1 FROM invoices i WHERE i.seller_id = cur.seller_id AND i.series = cur.series
                          AND i.fiscal_year = cur.fiscal_year AND i.serial >= cur.next_serial) THEN
                RAISE EXCEPTION 'invoice_number_counters: % % has an invoice above its counter', NEW.seller_id, NEW.series
                    USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            RETURN NULL;
        END
        $fn$;

        -- At COMMIT, per invoice (and per added line): a counter step stands behind it, and its lines 1..n sum to its totals.
        CREATE OR REPLACE FUNCTION invoices_commit_check() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        DECLARE
            inv invoices%ROWTYPE;
            n bigint; lo int; hi int; net bigint; sd bigint; vat bigint; total bigint;
        BEGIN
            IF TG_TABLE_NAME = 'invoices' THEN
                inv := NEW;
                IF NOT EXISTS (SELECT 1 FROM invoice_number_counters c WHERE c.seller_id = inv.seller_id AND c.series = inv.series
                                  AND c.fiscal_year = inv.fiscal_year AND c.next_serial > inv.serial AND c.last_issued_at >= inv.issued_at) THEN
                    RAISE EXCEPTION 'invoices: % has no counter step behind it', inv.number USING ERRCODE = 'integrity_constraint_violation';
                END IF;
            ELSE
                SELECT * INTO inv FROM invoices i WHERE i.id = NEW.invoice_id;
            END IF;
            SELECT count(*), min(line_no), max(line_no), sum(net_minor), sum(sd_minor), sum(vat_minor), sum(total_minor)
              INTO n, lo, hi, net, sd, vat, total FROM invoice_lines l WHERE l.invoice_id = inv.id;
            IF n = 0 OR lo <> 1 OR hi <> n OR (net, sd, vat, total) IS DISTINCT FROM (inv.net_minor, inv.sd_minor, inv.vat_minor, inv.total_minor) THEN
                RAISE EXCEPTION 'invoices: % needs lines 1..n whose sums are its totals', inv.number USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            RETURN NULL;
        END
        $fn$;

        DROP TRIGGER IF EXISTS trg_invoices_guard ON invoices;
        CREATE TRIGGER trg_invoices_guard BEFORE INSERT OR UPDATE OR DELETE ON invoices FOR EACH ROW EXECUTE FUNCTION invoices_guard();
        DROP TRIGGER IF EXISTS trg_invoices_no_truncate ON invoices;
        CREATE TRIGGER trg_invoices_no_truncate BEFORE TRUNCATE ON invoices FOR EACH STATEMENT EXECUTE FUNCTION invoices_guard();
        DROP TRIGGER IF EXISTS trg_invoice_lines_guard ON invoice_lines;
        CREATE TRIGGER trg_invoice_lines_guard BEFORE UPDATE OR DELETE ON invoice_lines FOR EACH ROW EXECUTE FUNCTION invoice_lines_guard();
        DROP TRIGGER IF EXISTS trg_invoice_lines_no_truncate ON invoice_lines;
        CREATE TRIGGER trg_invoice_lines_no_truncate BEFORE TRUNCATE ON invoice_lines FOR EACH STATEMENT EXECUTE FUNCTION invoice_lines_guard();

        DROP TRIGGER IF EXISTS trg_invoices_commit_check ON invoices;
        CREATE CONSTRAINT TRIGGER trg_invoices_commit_check AFTER INSERT ON invoices
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION invoices_commit_check();
        DROP TRIGGER IF EXISTS trg_invoice_lines_commit_check ON invoice_lines;
        CREATE CONSTRAINT TRIGGER trg_invoice_lines_commit_check AFTER INSERT ON invoice_lines
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION invoices_commit_check();
        DROP TRIGGER IF EXISTS trg_invoice_counter_step_check ON invoice_number_counters;
        CREATE CONSTRAINT TRIGGER trg_invoice_counter_step_check AFTER UPDATE ON invoice_number_counters
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION invoice_counter_step_check();
        """;

    public const string Uninstall = """
        DROP TRIGGER IF EXISTS trg_invoice_counter_step_check ON invoice_number_counters;
        DROP TRIGGER IF EXISTS trg_invoice_lines_commit_check ON invoice_lines;
        DROP TRIGGER IF EXISTS trg_invoices_commit_check ON invoices;
        DROP TRIGGER IF EXISTS trg_invoice_lines_no_truncate ON invoice_lines;
        DROP TRIGGER IF EXISTS trg_invoice_lines_guard ON invoice_lines;
        DROP TRIGGER IF EXISTS trg_invoices_no_truncate ON invoices;
        DROP TRIGGER IF EXISTS trg_invoices_guard ON invoices;
        DROP FUNCTION IF EXISTS invoice_counter_step_check();
        DROP FUNCTION IF EXISTS invoices_commit_check();
        DROP FUNCTION IF EXISTS invoice_lines_guard();
        DROP FUNCTION IF EXISTS invoices_guard();
        """;
}
