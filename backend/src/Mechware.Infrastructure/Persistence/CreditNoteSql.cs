namespace Mechware.Infrastructure.Persistence;

/// <summary>
/// Database half of "a credit note never exceeds the original" (ADR-0012 §9.2, §16; threat model §১৪ IT3):
/// <list type="bullet">
/// <item>At COMMIT (deferred constraint trigger on <c>invoice_lines</c>): a tax-invoice line credits nothing; a credit-note line credits a
/// line of its credit note's original invoice, of the same kind, order line and rates; the sums of quantity, value excl. VAT, SD, VAT and
/// total over all credit lines of that original line stay within it, and the credit-note totals of the original within its total. Credit
/// notes of one original share the seller's credit-note counter row (taken <c>FOR UPDATE</c> before COMMIT), so two parallel transactions
/// reach this check one after the other and the second sees the first's lines.</item>
/// <item><c>credit_note_requests</c>: a new request is Requested and names a tax invoice of its order; afterwards only one move out of
/// Requested (status, decision and the issued 6.7, which must be a credit note of that original, requested by the requester and issued
/// by the approver); DELETE and TRUNCATE refused.</item>
/// <item>E6.5c: at COMMIT a replacement 6.3 follows its fully credited predecessor (ADR-0012 §9.4).</item>
/// </list>
/// SQLSTATE 23000, messages start with the table. Idempotent: migration <c>AddCreditNoteRequests</c> runs it, tests that use EnsureCreated
/// run it after <see cref="InvoiceSql"/>.
/// </summary>
public static class CreditNoteSql
{
    public const string Install = """
        CREATE OR REPLACE FUNCTION invoice_credit_caps_check() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        DECLARE
            inv invoices%ROWTYPE; o invoice_lines%ROWTYPE; q bigint; net bigint; sd bigint; vat bigint; total bigint;
        BEGIN
            SELECT * INTO inv FROM invoices i WHERE i.id = NEW.invoice_id;
            IF inv.kind = 'TaxInvoice' THEN
                IF NEW.original_line_id IS NOT NULL THEN
                    RAISE EXCEPTION 'invoice_lines: a line of tax invoice % credits nothing', inv.number USING ERRCODE = 'integrity_constraint_violation';
                END IF;
                RETURN NULL;
            END IF;
            SELECT * INTO o FROM invoice_lines l WHERE l.id = NEW.original_line_id AND l.invoice_id = inv.original_invoice_id;
            IF NOT FOUND OR o.kind <> NEW.kind OR o.order_item_id IS DISTINCT FROM NEW.order_item_id OR o.vat_rate <> NEW.vat_rate OR o.sd_rate <> NEW.sd_rate THEN
                RAISE EXCEPTION 'invoice_lines: a line of credit note % credits a line of its original invoice (same kind, order line and rates)', inv.number
                    USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            SELECT sum(l.quantity), sum(l.net_minor), sum(l.sd_minor), sum(l.vat_minor), sum(l.total_minor)
              INTO q, net, sd, vat, total FROM invoice_lines l WHERE l.original_line_id = o.id;
            IF q > o.quantity OR net > o.net_minor OR sd > o.sd_minor OR vat > o.vat_minor OR total > o.total_minor THEN
                RAISE EXCEPTION 'invoice_lines: credit notes of line % exceed the original (quantity %/%, value %/%, VAT %/%)',
                    o.line_no, q, o.quantity, net, o.net_minor, vat, o.vat_minor USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF (SELECT sum(c.total_minor) FROM invoices c WHERE c.original_invoice_id = inv.original_invoice_id AND c.kind = 'CreditNote')
               > (SELECT i.total_minor FROM invoices i WHERE i.id = inv.original_invoice_id) THEN
                RAISE EXCEPTION 'invoice_lines: credit notes exceed the total of their original invoice' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            RETURN NULL;
        END
        $fn$;

        CREATE OR REPLACE FUNCTION credit_note_requests_guard() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        DECLARE
            decision text[] := ARRAY['status', 'decided_by', 'decided_at', 'decision_reason', 'issued_invoice_id', 'updated_at'];
        BEGIN
            IF TG_OP IN ('TRUNCATE', 'DELETE') THEN
                RAISE EXCEPTION '%: credit-note requests are never deleted', TG_TABLE_NAME USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF TG_OP = 'INSERT' THEN
                IF NEW.status <> 'Requested' OR NOT EXISTS (
                    SELECT 1 FROM invoices i WHERE i.id = NEW.original_invoice_id AND i.kind = 'TaxInvoice' AND i.order_id = NEW.order_id) THEN
                    RAISE EXCEPTION 'credit_note_requests: a new request asks to credit a tax invoice of its order'
                        USING ERRCODE = 'integrity_constraint_violation';
                END IF;
                RETURN NEW;
            END IF;
            IF OLD.status <> 'Requested' OR NEW.status = 'Requested' OR (to_jsonb(NEW) - decision) IS DISTINCT FROM (to_jsonb(OLD) - decision) THEN
                RAISE EXCEPTION 'credit_note_requests: a request moves once out of Requested and its asked content never changes'
                    USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF NEW.status = 'Approved' AND NOT EXISTS (
                SELECT 1 FROM invoices c WHERE c.id = NEW.issued_invoice_id AND c.kind = 'CreditNote' AND c.original_invoice_id = NEW.original_invoice_id
                   AND c.requested_by = NEW.requested_by AND c.issued_by = NEW.decided_by) THEN
                RAISE EXCEPTION 'credit_note_requests: an approval names the credit note it issued' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;

        -- E6.5c (§9.4): a replacement 6.3 names the previous tax invoice of its shipment and seller (replacement_no - 1, same mode, order and
        -- total: the lines are copied) and that one is fully credited (its credit notes sum to its total) by COMMIT.
        CREATE OR REPLACE FUNCTION invoice_replacement_check() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        DECLARE
            prev invoices%ROWTYPE; credited bigint;
        BEGIN
            SELECT * INTO prev FROM invoices i WHERE i.id = NEW.replaces_invoice_id;
            IF NOT FOUND OR prev.kind <> 'TaxInvoice' OR prev.mode <> NEW.mode OR prev.shipment_id IS DISTINCT FROM NEW.shipment_id
               OR prev.seller_id <> NEW.seller_id OR prev.order_id <> NEW.order_id OR prev.replacement_no <> NEW.replacement_no - 1
               OR prev.total_minor <> NEW.total_minor THEN
                RAISE EXCEPTION 'invoices: replacement % names the previous tax invoice of its shipment and seller, with the same total', NEW.number
                    USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            SELECT COALESCE(sum(c.total_minor), 0) INTO credited FROM invoices c WHERE c.original_invoice_id = prev.id AND c.kind = 'CreditNote';
            IF credited <> prev.total_minor THEN
                RAISE EXCEPTION 'invoices: % replaces % before it is fully credited (% of %)', NEW.number, prev.number, credited, prev.total_minor
                    USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            RETURN NULL;
        END
        $fn$;

        DROP TRIGGER IF EXISTS trg_invoice_replacement_check ON invoices;
        CREATE CONSTRAINT TRIGGER trg_invoice_replacement_check AFTER INSERT ON invoices DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW WHEN (NEW.kind = 'TaxInvoice' AND NEW.replacement_no > 0) EXECUTE FUNCTION invoice_replacement_check();
        DROP TRIGGER IF EXISTS trg_invoice_credit_caps_check ON invoice_lines;
        CREATE CONSTRAINT TRIGGER trg_invoice_credit_caps_check AFTER INSERT ON invoice_lines
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION invoice_credit_caps_check();
        DROP TRIGGER IF EXISTS trg_credit_note_requests_guard ON credit_note_requests;
        CREATE TRIGGER trg_credit_note_requests_guard BEFORE INSERT OR UPDATE OR DELETE ON credit_note_requests
            FOR EACH ROW EXECUTE FUNCTION credit_note_requests_guard();
        DROP TRIGGER IF EXISTS trg_credit_note_requests_no_truncate ON credit_note_requests;
        CREATE TRIGGER trg_credit_note_requests_no_truncate BEFORE TRUNCATE ON credit_note_requests
            FOR EACH STATEMENT EXECUTE FUNCTION credit_note_requests_guard();
        """;

    /// <summary>E6.5c only (migration <c>AddReplacementAndReturnRetry</c> Down): the replacement check, nothing of E6.5a.</summary>
    public const string UninstallReplacement = """
        DROP TRIGGER IF EXISTS trg_invoice_replacement_check ON invoices;
        DROP FUNCTION IF EXISTS invoice_replacement_check();
        """;

    public const string Uninstall = """
        DROP TRIGGER IF EXISTS trg_invoice_replacement_check ON invoices;
        DROP FUNCTION IF EXISTS invoice_replacement_check();
        DROP TRIGGER IF EXISTS trg_credit_note_requests_no_truncate ON credit_note_requests;
        DROP TRIGGER IF EXISTS trg_credit_note_requests_guard ON credit_note_requests;
        DROP TRIGGER IF EXISTS trg_invoice_credit_caps_check ON invoice_lines;
        DROP FUNCTION IF EXISTS credit_note_requests_guard();
        DROP FUNCTION IF EXISTS invoice_credit_caps_check();
        """;
}
