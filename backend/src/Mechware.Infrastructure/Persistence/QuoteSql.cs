namespace Mechware.Infrastructure.Persistence;

/// <summary>
/// Database backstop for RFQs and quotes (ADR-0011 §6, §8.6, §11; threat model §১৩ QT3, QT4, QT7): a code bug or a hand-written
/// query must not be enough to change an issued quote, approve one's own version or give one version two orders.
/// <list type="bullet">
/// <item><c>rfqs</c>: created Submitted; moves only along §2; each step writes only its own columns; never deleted.
/// <c>rfq_lines</c>: append-only, and only while the RFQ is Submitted.</item>
/// <item><c>quotes</c>: number, company, RFQ and author never change; a closed quote never changes; never deleted.</item>
/// <item><c>quote_versions</c>: created as a Draft of an open quote; moves only along §3, each step writing only its own
/// columns (a whitelist, so a column added later is frozen by default); the approver is no contributor; the accepted order
/// moves, and an Accepted version is superseded, only after its order lapsed before confirmation; the PDF columns are written
/// once; never deleted. One live (Sent | Accepted) version per quote is a DEFERRED exclusion constraint, because a send
/// supersedes the live version in the same save and EF may write the two rows in either order.</item>
/// <item><c>quote_lines</c>: changed only while the version is a Draft of an open quote; while it is PendingApproval only the
/// staff-only check columns (the approval rewrites them). EF writes <c>quote_lines</c> before <c>quote_versions</c> (commands
/// of independent rows are ordered by table name), so a send or an approval sees its version still Draft or PendingApproval;
/// the round-trip tests pin that order, and a change of it fails closed.</item>
/// <item><c>quote_version_contributors</c>: added only while the version is a Draft of an open quote; never changed or removed.</item>
/// </list>
/// Errors use SQLSTATE 23000 with a message starting with the table name; TRUNCATE is refused on all six tables. The SQL is
/// idempotent: the migration runs it, and tests that build their schema with EnsureCreated run it too.
/// </summary>
public static class QuoteSql
{
    public static string Install { get; } = """
        CREATE OR REPLACE FUNCTION quotes_no_truncate() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        BEGIN
            RAISE EXCEPTION '%: quote records are never truncated', TG_TABLE_NAME USING ERRCODE = 'integrity_constraint_violation';
        END
        $fn$;

        CREATE OR REPLACE FUNCTION rfqs_guard() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        DECLARE
            allowed text[];
        BEGIN
            IF TG_OP = 'INSERT' THEN
                IF NEW.status <> 'Submitted' THEN
                    RAISE EXCEPTION 'rfqs: an RFQ is created as Submitted' USING ERRCODE = 'integrity_constraint_violation';
                END IF;
                RETURN NEW;
            END IF;
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'rfqs: RFQs are never deleted' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            allowed := CASE
                WHEN OLD.status IN ('Closed', 'Withdrawn') AND NEW.status = OLD.status THEN ARRAY['updated_at']
                WHEN NEW.status = OLD.status THEN ARRAY['updated_at', 'assigned_to']
                WHEN OLD.status = 'Submitted' AND NEW.status = 'Quoting' THEN ARRAY['updated_at', 'assigned_to', 'status']
                WHEN OLD.status = 'Quoting' AND NEW.status = 'Answered' THEN ARRAY['updated_at', 'assigned_to', 'status', 'first_quote_sent_at']
                WHEN OLD.status IN ('Submitted', 'Quoting', 'Answered') AND NEW.status = 'Closed'
                    THEN ARRAY['updated_at', 'assigned_to', 'status', 'closed_by', 'closed_at', 'close_reason']
                WHEN OLD.status IN ('Submitted', 'Quoting', 'Answered') AND NEW.status = 'Withdrawn'
                    THEN ARRAY['updated_at', 'assigned_to', 'status', 'withdrawn_at']
            END;
            IF allowed IS NULL THEN
                RAISE EXCEPTION 'rfqs: % cannot become %', OLD.status, NEW.status USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF (to_jsonb(NEW) - allowed) IS DISTINCT FROM (to_jsonb(OLD) - allowed) THEN
                RAISE EXCEPTION 'rfqs: a % RFQ changes only by its next step; company, requester, source, number and request never change', OLD.status
                    USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE OR REPLACE FUNCTION rfq_lines_guard() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        BEGIN
            IF TG_OP = 'INSERT' AND EXISTS (SELECT 1 FROM rfqs r WHERE r.id = NEW.rfq_id AND r.status = 'Submitted') THEN
                RETURN NEW;
            END IF;
            RAISE EXCEPTION 'rfq_lines: RFQ lines are append-only and written with their RFQ' USING ERRCODE = 'integrity_constraint_violation';
        END
        $fn$;

        CREATE OR REPLACE FUNCTION quotes_guard() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        BEGIN
            IF TG_OP = 'INSERT' THEN
                IF NEW.closed_at IS NOT NULL THEN
                    RAISE EXCEPTION 'quotes: a quote is created open' USING ERRCODE = 'integrity_constraint_violation';
                END IF;
                RETURN NEW;
            END IF;
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'quotes: quotes are never deleted' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF (NEW.id, NEW.number, NEW.company_id, NEW.rfq_id, NEW.created_by, NEW.created_at)
               IS DISTINCT FROM (OLD.id, OLD.number, OLD.company_id, OLD.rfq_id, OLD.created_by, OLD.created_at) THEN
                RAISE EXCEPTION 'quotes: number, company, RFQ and author never change' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF OLD.closed_at IS NOT NULL AND (to_jsonb(NEW) - 'updated_at') IS DISTINCT FROM (to_jsonb(OLD) - 'updated_at') THEN
                RAISE EXCEPTION 'quotes: a closed quote never changes' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF NEW.latest_version_no < OLD.latest_version_no THEN
                RAISE EXCEPTION 'quotes: versions are only added' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE OR REPLACE FUNCTION quote_versions_guard() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        DECLARE
            allowed text[];
            content text[] := ARRAY['validity_days', 'payment_terms', 'shipping_mode', 'ship_to_site_id', 'ship_to', 'shipping_fees', 'terms_text',
                'buyer_note', 'internal_note', 'goods_net_minor', 'tax_minor', 'goods_total_minor', 'shipping_total_minor', 'grand_total_minor'];
            frozen text[] := ARRAY['submitted_by', 'submitted_at', 'company_snapshot', 'preparer_name', 'content_hash', 'flags'];
            sent text[] := ARRAY['sent_by', 'sent_at', 'valid_until_utc'];
            pdf text[] := ARRAY['pdf_en_key', 'pdf_en_sha256', 'pdf_bn_key', 'pdf_bn_sha256'];
            accepted text[] := ARRAY['accepted_order_id', 'accepted_by', 'accepted_at'];
            closed boolean;
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'quote_versions: versions are never deleted' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            SELECT q.closed_at IS NOT NULL INTO closed FROM quotes q WHERE q.id = NEW.quote_id;
            IF TG_OP = 'INSERT' THEN
                IF NEW.status <> 'Draft' THEN
                    RAISE EXCEPTION 'quote_versions: a version is created as a Draft' USING ERRCODE = 'integrity_constraint_violation';
                END IF;
                IF closed THEN
                    RAISE EXCEPTION 'quote_versions: the quote is closed; it gets no new version' USING ERRCODE = 'integrity_constraint_violation';
                END IF;
                RETURN NEW;
            END IF;

            allowed := CASE
                WHEN OLD.status = 'Draft' AND NEW.status = 'Draft' THEN ARRAY['updated_at'] || content
                WHEN OLD.status = 'Draft' AND NEW.status = 'PendingApproval' THEN ARRAY['updated_at', 'status'] || content || frozen
                WHEN OLD.status = 'Draft' AND NEW.status = 'Sent' THEN ARRAY['updated_at', 'status'] || content || frozen || sent
                WHEN OLD.status = 'PendingApproval' AND NEW.status = 'PendingApproval' THEN ARRAY['updated_at']
                WHEN OLD.status = 'PendingApproval' AND NEW.status = 'Sent'
                    THEN ARRAY['updated_at', 'status', 'flags', 'approved_by', 'approved_at', 'approval_comment'] || sent
                WHEN OLD.status = 'PendingApproval' AND NEW.status = 'Draft'
                    THEN ARRAY['updated_at', 'status', 'submitted_by', 'submitted_at', 'content_hash', 'flags', 'approval_rejected_by',
                        'approval_rejected_at', 'approval_comment']
                WHEN OLD.status = 'Sent' AND NEW.status = 'Accepted' THEN ARRAY['updated_at', 'status'] || accepted
                WHEN OLD.status = 'Sent' AND NEW.status = 'Rejected' THEN ARRAY['updated_at', 'status', 'rejected_by', 'rejected_at', 'reject_reason']
                WHEN OLD.status = 'Sent' AND NEW.status = 'Withdrawn' THEN ARRAY['updated_at', 'status', 'withdrawn_by', 'withdrawn_at', 'withdraw_reason']
                WHEN OLD.status IN ('Sent', 'Accepted') AND NEW.status = 'Superseded' THEN ARRAY['updated_at', 'status', 'superseded_at']
                WHEN OLD.status = 'Accepted' AND NEW.status = 'Accepted' THEN ARRAY['updated_at'] || accepted || pdf
                WHEN OLD.status = NEW.status AND OLD.status IN ('Sent', 'Rejected', 'Withdrawn', 'Superseded') THEN ARRAY['updated_at'] || pdf
            END;
            IF allowed IS NULL THEN
                RAISE EXCEPTION 'quote_versions: % cannot become %', OLD.status, NEW.status USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF (to_jsonb(NEW) - allowed) IS DISTINCT FROM (to_jsonb(OLD) - allowed) THEN
                RAISE EXCEPTION 'quote_versions: a % version cannot change outside its next step; revise the quote', OLD.status
                    USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            -- Closing withdraws the Sent version and recalls a pending one; nothing else moves on a closed quote but a PDF record.
            IF closed AND NOT ((OLD.status = 'Sent' AND NEW.status = 'Withdrawn') OR (OLD.status = 'PendingApproval' AND NEW.status = 'Draft')
                               OR (OLD.status = NEW.status AND OLD.status <> 'Draft'
                                   AND (to_jsonb(NEW) - (ARRAY['updated_at'] || pdf)) = (to_jsonb(OLD) - (ARRAY['updated_at'] || pdf)))) THEN
                RAISE EXCEPTION 'quote_versions: the quote is closed' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF (OLD.pdf_en_key IS NOT NULL AND (NEW.pdf_en_key, NEW.pdf_en_sha256) IS DISTINCT FROM (OLD.pdf_en_key, OLD.pdf_en_sha256))
               OR (OLD.pdf_bn_key IS NOT NULL AND (NEW.pdf_bn_key, NEW.pdf_bn_sha256) IS DISTINCT FROM (OLD.pdf_bn_key, OLD.pdf_bn_sha256)) THEN
                RAISE EXCEPTION 'quote_versions: a stored PDF is recorded once' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF OLD.status = 'PendingApproval' AND NEW.status = 'Sent' AND EXISTS (
                SELECT 1 FROM quote_version_contributors c WHERE c.version_id = NEW.id AND c.user_id = NEW.approved_by) THEN
                RAISE EXCEPTION 'quote_versions: whoever created or changed a version cannot approve it' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            -- §8.6: the order moves (and an Accepted version is superseded) only away from a Cancelled order that was never confirmed.
            IF OLD.status = 'Accepted' AND (NEW.status <> 'Accepted'
                    OR (NEW.accepted_order_id, NEW.accepted_by, NEW.accepted_at) IS DISTINCT FROM (OLD.accepted_order_id, OLD.accepted_by, OLD.accepted_at))
               AND NOT EXISTS (SELECT 1 FROM orders o WHERE o.id = OLD.accepted_order_id AND o.status = 'Cancelled' AND o.confirmed_at IS NULL) THEN
                RAISE EXCEPTION 'quote_versions: an accepted version moves on only after its order lapsed before confirmation'
                    USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE OR REPLACE FUNCTION quote_lines_guard() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        DECLARE
            version_status text;
            closed boolean;
            checks text[] := ARRAY['reference_unit_price_minor', 'reference_source', 'cost_minor_at_check', 'floor_minor_at_check', 'below_floor', 'over_authority'];
        BEGIN
            IF TG_OP = 'UPDATE' AND NEW.version_id IS DISTINCT FROM OLD.version_id THEN
                RAISE EXCEPTION 'quote_lines: a line never moves to another version' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            -- FOR SHARE: waits for a concurrent send or approval of the version, then sees its new status.
            SELECT v.status, q.closed_at IS NOT NULL INTO version_status, closed
            FROM quote_versions v JOIN quotes q ON q.id = v.quote_id
            WHERE v.id = CASE WHEN TG_OP = 'DELETE' THEN OLD.version_id ELSE NEW.version_id END
            FOR SHARE OF v;
            IF closed THEN
                RAISE EXCEPTION 'quote_lines: the quote is closed' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF version_status = 'Draft' THEN
                IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
                RETURN NEW;
            END IF;
            IF TG_OP = 'UPDATE' AND version_status = 'PendingApproval' AND (to_jsonb(NEW) - checks) = (to_jsonb(OLD) - checks) THEN
                RETURN NEW;
            END IF;
            RAISE EXCEPTION 'quote_lines: lines of a % version cannot change; revise the quote', version_status
                USING ERRCODE = 'integrity_constraint_violation';
        END
        $fn$;

        CREATE OR REPLACE FUNCTION quote_version_contributors_guard() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        DECLARE
            version_status text;
            closed boolean;
        BEGIN
            IF TG_OP = 'UPDATE' THEN
                RAISE EXCEPTION 'quote_version_contributors: contributors never change' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'quote_version_contributors: contributors are never removed' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            SELECT v.status, q.closed_at IS NOT NULL INTO version_status, closed
            FROM quote_versions v JOIN quotes q ON q.id = v.quote_id WHERE v.id = NEW.version_id FOR SHARE OF v;
            IF closed THEN
                RAISE EXCEPTION 'quote_version_contributors: the quote is closed' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF version_status IS DISTINCT FROM 'Draft' THEN
                RAISE EXCEPTION 'quote_version_contributors: contributors are recorded only while a version is a Draft'
                    USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;

        DO $do$
        DECLARE
            t text;
        BEGIN
            FOREACH t IN ARRAY ARRAY['rfqs', 'rfq_lines', 'quotes', 'quote_versions', 'quote_lines', 'quote_version_contributors'] LOOP
                EXECUTE format('DROP TRIGGER IF EXISTS %I ON %I', 'trg_' || t || '_guard', t);
                EXECUTE format('CREATE TRIGGER %I BEFORE INSERT OR UPDATE OR DELETE ON %I FOR EACH ROW EXECUTE FUNCTION %I()',
                    'trg_' || t || '_guard', t, t || '_guard');
                EXECUTE format('DROP TRIGGER IF EXISTS %I ON %I', 'trg_' || t || '_no_truncate', t);
                EXECUTE format('CREATE TRIGGER %I BEFORE TRUNCATE ON %I FOR EACH STATEMENT EXECUTE FUNCTION quotes_no_truncate()',
                    'trg_' || t || '_no_truncate', t);
            END LOOP;
            IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ux_quote_versions_one_live') THEN
                ALTER TABLE quote_versions ADD CONSTRAINT ux_quote_versions_one_live
                    EXCLUDE USING btree (quote_id WITH =) WHERE (status IN ('Sent', 'Accepted')) DEFERRABLE INITIALLY DEFERRED;
            END IF;
        END
        $do$;
        """;

    /// <summary>Removes what <see cref="Install"/> adds outside the tables (the migration's Down drops the tables first).</summary>
    public static string Uninstall { get; } = """
        DROP FUNCTION IF EXISTS quote_version_contributors_guard();
        DROP FUNCTION IF EXISTS quote_lines_guard();
        DROP FUNCTION IF EXISTS quote_versions_guard();
        DROP FUNCTION IF EXISTS quotes_guard();
        DROP FUNCTION IF EXISTS rfq_lines_guard();
        DROP FUNCTION IF EXISTS rfqs_guard();
        DROP FUNCTION IF EXISTS quotes_no_truncate();
        """;
}
