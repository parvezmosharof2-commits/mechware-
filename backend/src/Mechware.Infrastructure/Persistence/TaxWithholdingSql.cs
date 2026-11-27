namespace Mechware.Infrastructure.Persistence;

/// <summary>
/// Database half of the VDS/AIT rules (ADR-0012 §11, §15, §16; threat model IT14, I8):
/// <list type="bullet">
/// <item>At COMMIT (deferred constraint trigger on insert): the certificate names a tax invoice of its company and order, and the recorded or
/// verified certificates of its kind stay within the cap: VDS ≤ the invoice VAT less the VAT of its credit notes, AIT ≤ its value excl. VAT less
/// the credited value. The trigger first takes the invoice row <c>FOR NO KEY UPDATE</c> (the lock a credit-note approval and E6.6b take as
/// well), so a parallel certificate or credit note of that invoice is counted after it commits.</item>
/// <item>Guard: a new certificate is Recorded; afterwards only one move out of Recorded (status and decision); the typed facts never change;
/// DELETE and TRUNCATE refused (retention, ADR §17).</item>
/// </list>
/// SQLSTATE 23000, messages start with the table. Idempotent: migration <c>AddTaxWithholdings</c> runs it, tests that use EnsureCreated run it
/// after <see cref="CreditNoteSql"/>.
/// </summary>
public static class TaxWithholdingSql
{
    public const string Install = """
        CREATE OR REPLACE FUNCTION tax_withholdings_caps_check() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        DECLARE
            inv invoices%ROWTYPE; credited_net bigint; credited_vat bigint; withheld bigint; cap bigint;
        BEGIN
            SELECT * INTO inv FROM invoices i WHERE i.id = NEW.invoice_id FOR NO KEY UPDATE;
            IF NOT FOUND OR inv.kind <> 'TaxInvoice' OR inv.company_id IS DISTINCT FROM NEW.company_id OR inv.order_id <> NEW.order_id THEN
                RAISE EXCEPTION 'tax_withholdings: certificate % names a tax invoice of its company and order', NEW.certificate_number
                    USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            SELECT COALESCE(sum(c.net_minor), 0), COALESCE(sum(c.vat_minor), 0) INTO credited_net, credited_vat
              FROM invoices c WHERE c.original_invoice_id = inv.id AND c.kind = 'CreditNote';
            SELECT COALESCE(sum(w.amount_minor), 0) INTO withheld
              FROM tax_withholdings w WHERE w.invoice_id = inv.id AND w.kind = NEW.kind AND w.status <> 'Rejected';
            cap := CASE NEW.kind WHEN 'Vds' THEN inv.vat_minor - credited_vat ELSE inv.net_minor - credited_net END;
            IF withheld > cap THEN
                RAISE EXCEPTION 'tax_withholdings: % certificates of invoice % exceed what is left after credit notes (% of %)', NEW.kind, inv.number, withheld, cap
                    USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            RETURN NULL;
        END
        $fn$;

        CREATE OR REPLACE FUNCTION tax_withholdings_guard() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        DECLARE
            decision text[] := ARRAY['status', 'decided_by', 'decided_at', 'decision_reason', 'updated_at'];
        BEGIN
            IF TG_OP IN ('TRUNCATE', 'DELETE') THEN
                RAISE EXCEPTION '%: withholding certificates are never deleted', TG_TABLE_NAME USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF TG_OP = 'INSERT' THEN
                IF NEW.status <> 'Recorded' THEN
                    RAISE EXCEPTION 'tax_withholdings: a new certificate is Recorded' USING ERRCODE = 'integrity_constraint_violation';
                END IF;
                RETURN NEW;
            END IF;
            IF OLD.status <> 'Recorded' OR NEW.status = 'Recorded' OR (to_jsonb(NEW) - decision) IS DISTINCT FROM (to_jsonb(OLD) - decision) THEN
                RAISE EXCEPTION 'tax_withholdings: a certificate moves once out of Recorded and its facts never change'
                    USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;

        DROP TRIGGER IF EXISTS trg_tax_withholdings_caps_check ON tax_withholdings;
        CREATE CONSTRAINT TRIGGER trg_tax_withholdings_caps_check AFTER INSERT ON tax_withholdings
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION tax_withholdings_caps_check();
        DROP TRIGGER IF EXISTS trg_tax_withholdings_guard ON tax_withholdings;
        CREATE TRIGGER trg_tax_withholdings_guard BEFORE INSERT OR UPDATE OR DELETE ON tax_withholdings
            FOR EACH ROW EXECUTE FUNCTION tax_withholdings_guard();
        DROP TRIGGER IF EXISTS trg_tax_withholdings_no_truncate ON tax_withholdings;
        CREATE TRIGGER trg_tax_withholdings_no_truncate BEFORE TRUNCATE ON tax_withholdings
            FOR EACH STATEMENT EXECUTE FUNCTION tax_withholdings_guard();
        """;

    public const string Uninstall = """
        DROP TRIGGER IF EXISTS trg_tax_withholdings_no_truncate ON tax_withholdings;
        DROP TRIGGER IF EXISTS trg_tax_withholdings_guard ON tax_withholdings;
        DROP TRIGGER IF EXISTS trg_tax_withholdings_caps_check ON tax_withholdings;
        DROP FUNCTION IF EXISTS tax_withholdings_guard();
        DROP FUNCTION IF EXISTS tax_withholdings_caps_check();
        """;
}
