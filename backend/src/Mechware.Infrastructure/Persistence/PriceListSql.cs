using System.Globalization;
using Mechware.Domain.Pricing.PriceLists;

namespace Mechware.Infrastructure.Persistence;

/// <summary>
/// Database backstop for price lists (ADR-0008 §3, threat model §৮ PL1/PL4): a code bug must not be enough to change an
/// approved price or to approve one's own list. Triggers, because CHECKs cannot see the previous row or other tables:
/// <list type="bullet">
/// <item><c>price_lists</c>: created only as Draft; id, series, version, scope, company, currency and author never change;
/// Draft → Pending → Approved → Withdrawn (Pending → Draft is a rejection) and nothing else; a pending list changes only by
/// its approval or rejection; an approved list only by its withdrawal; a withdrawn one never; the approver is not a
/// contributor; approved and withdrawn lists are never deleted.</item>
/// <item><c>price_list_entries</c>: insert, change and delete only while the list is a Draft; afterwards only the approval
/// writes the audit values (cost, floor, below floor) once.</item>
/// <item><c>price_list_contributors</c>: added only while the list is a Draft, never changed or removed (only cascaded).</item>
/// </list>
/// Errors use SQLSTATE 23000 with a message starting with the table name. The SQL is idempotent: the migration runs it, and
/// tests that build their schema with EnsureCreated run it too. It also seeds the one global margin floor (owner decision 3).
/// </summary>
public static class PriceListSql
{
    /// <summary>Fixed id of the seeded global row, so a re-run inserts nothing.</summary>
    public static readonly Guid GlobalMarginFloorId = new("6d2f0a4e-3b1c-4c8e-9f57-0d24f1000001");

    public static string Install { get; } = """
        CREATE OR REPLACE FUNCTION price_lists_guard() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        DECLARE
            allowed text[];
        BEGIN
            IF TG_OP = 'INSERT' THEN
                IF NEW.status <> 'Draft' THEN
                    RAISE EXCEPTION 'price_lists: a price list is created as a Draft' USING ERRCODE = 'integrity_constraint_violation';
                END IF;
                RETURN NEW;
            END IF;
            IF TG_OP = 'DELETE' THEN
                IF OLD.status <> 'Draft' THEN
                    RAISE EXCEPTION 'price_lists: only a Draft can be deleted' USING ERRCODE = 'integrity_constraint_violation';
                END IF;
                RETURN OLD;
            END IF;

            IF (NEW.id, NEW.series_id, NEW.version, NEW.scope, NEW.company_id, NEW.currency, NEW.created_by, NEW.created_at)
               IS DISTINCT FROM
               (OLD.id, OLD.series_id, OLD.version, OLD.scope, OLD.company_id, OLD.currency, OLD.created_by, OLD.created_at) THEN
                RAISE EXCEPTION 'price_lists: identity, scope and company never change' USING ERRCODE = 'integrity_constraint_violation';
            END IF;

            IF OLD.status = 'Draft' THEN
                IF NEW.status NOT IN ('Draft', 'PendingApproval') THEN
                    RAISE EXCEPTION 'price_lists: a Draft is submitted before anything else' USING ERRCODE = 'integrity_constraint_violation';
                END IF;
                RETURN NEW;
            END IF;

            allowed := CASE
                WHEN OLD.status = 'PendingApproval' AND NEW.status = 'PendingApproval' THEN ARRAY['updated_at']
                WHEN OLD.status = 'PendingApproval' AND NEW.status = 'Approved'
                    THEN ARRAY['updated_at', 'status', 'approved_by', 'approved_at', 'effective_from_utc']
                WHEN OLD.status = 'PendingApproval' AND NEW.status = 'Draft'
                    THEN ARRAY['updated_at', 'status', 'submitted_by', 'submitted_at', 'content_hash', 'rejected_by', 'rejected_at', 'rejection_reason']
                WHEN OLD.status = 'Approved' AND NEW.status = 'Approved' THEN ARRAY['updated_at']
                WHEN OLD.status = 'Approved' AND NEW.status = 'Withdrawn'
                    THEN ARRAY['updated_at', 'status', 'withdrawn_by', 'withdrawn_at', 'withdraw_reason']
                WHEN OLD.status = 'Withdrawn' AND NEW.status = 'Withdrawn' THEN ARRAY['updated_at']
            END;
            IF allowed IS NULL THEN
                RAISE EXCEPTION 'price_lists: % cannot become %', OLD.status, NEW.status USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF (to_jsonb(NEW) - allowed) IS DISTINCT FROM (to_jsonb(OLD) - allowed) THEN
                RAISE EXCEPTION 'price_lists: a % price list cannot change; revise it', OLD.status USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF NEW.status = 'Approved' AND OLD.status = 'PendingApproval' AND EXISTS (
                SELECT 1 FROM price_list_contributors c WHERE c.price_list_id = NEW.id AND c.user_id = NEW.approved_by) THEN
                RAISE EXCEPTION 'price_lists: whoever created or changed a price list cannot approve it'
                    USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;

        DROP TRIGGER IF EXISTS trg_price_lists_guard ON price_lists;
        CREATE TRIGGER trg_price_lists_guard BEFORE INSERT OR UPDATE OR DELETE ON price_lists
            FOR EACH ROW EXECUTE FUNCTION price_lists_guard();

        CREATE OR REPLACE FUNCTION price_list_entries_guard() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        DECLARE
            old_status text;
            new_status text;
            audit text[] := ARRAY['cost_minor_at_approval', 'floor_minor_at_approval', 'below_floor_at_approval'];
        BEGIN
            -- FOR SHARE: waits for a concurrent submit/approve of the list, then sees its new status.
            IF TG_OP IN ('UPDATE', 'DELETE') THEN
                SELECT l.status INTO old_status FROM price_lists l WHERE l.id = OLD.price_list_id FOR SHARE;
            END IF;
            IF TG_OP = 'DELETE' THEN
                -- No parent row: the cascade of deleting a Draft.
                IF old_status IS NOT NULL AND old_status <> 'Draft' THEN
                    RAISE EXCEPTION 'price_list_entries: entries of a % price list cannot be removed', old_status
                        USING ERRCODE = 'integrity_constraint_violation';
                END IF;
                RETURN OLD;
            END IF;

            SELECT l.status INTO new_status FROM price_lists l WHERE l.id = NEW.price_list_id FOR SHARE;
            IF TG_OP = 'INSERT' THEN
                IF new_status IS DISTINCT FROM 'Draft' THEN
                    RAISE EXCEPTION 'price_list_entries: entries are added only to a Draft' USING ERRCODE = 'integrity_constraint_violation';
                END IF;
                RETURN NEW;
            END IF;

            IF old_status = 'Draft' AND new_status = 'Draft' THEN
                RETURN NEW;
            END IF;
            -- The approval writes cost, floor and below-floor once (the list may already show Approved in this transaction).
            IF old_status IN ('PendingApproval', 'Approved') AND NEW.price_list_id = OLD.price_list_id
               AND OLD.below_floor_at_approval IS NULL AND NEW.below_floor_at_approval IS NOT NULL
               AND (to_jsonb(NEW) - audit) = (to_jsonb(OLD) - audit) THEN
                RETURN NEW;
            END IF;
            RAISE EXCEPTION 'price_list_entries: entries of a % price list cannot change', old_status
                USING ERRCODE = 'integrity_constraint_violation';
        END
        $fn$;

        DROP TRIGGER IF EXISTS trg_price_list_entries_guard ON price_list_entries;
        CREATE TRIGGER trg_price_list_entries_guard BEFORE INSERT OR UPDATE OR DELETE ON price_list_entries
            FOR EACH ROW EXECUTE FUNCTION price_list_entries_guard();

        CREATE OR REPLACE FUNCTION price_list_contributors_guard() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        DECLARE
            list_status text;
        BEGIN
            IF TG_OP = 'UPDATE' THEN
                RAISE EXCEPTION 'price_list_contributors: contributors never change' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF TG_OP = 'DELETE' THEN
                IF EXISTS (SELECT 1 FROM price_lists l WHERE l.id = OLD.price_list_id) THEN
                    RAISE EXCEPTION 'price_list_contributors: contributors are kept as long as their list exists'
                        USING ERRCODE = 'integrity_constraint_violation';
                END IF;
                RETURN OLD;
            END IF;
            SELECT l.status INTO list_status FROM price_lists l WHERE l.id = NEW.price_list_id FOR SHARE;
            IF list_status IS DISTINCT FROM 'Draft' THEN
                RAISE EXCEPTION 'price_list_contributors: contributors are recorded only while a list is a Draft'
                    USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;

        DROP TRIGGER IF EXISTS trg_price_list_contributors_guard ON price_list_contributors;
        CREATE TRIGGER trg_price_list_contributors_guard BEFORE INSERT OR UPDATE OR DELETE ON price_list_contributors
            FOR EACH ROW EXECUTE FUNCTION price_list_contributors_guard();

        """ + $"""
        -- Owner decision 3: one global margin floor of {Percent} % on cost. Reference data; the owner changes it in D2.9.
        INSERT INTO margin_floors (id, scope, brand_id, category_id, min_markup_percent, updated_by, created_at, updated_at)
        VALUES ('{GlobalMarginFloorId}', 'Global', NULL, NULL, {Percent}, NULL, now(), now())
        ON CONFLICT DO NOTHING;
        """;

    private static string Percent => MarginFloorPolicy.DefaultGlobalMinMarkupPercent.ToString(CultureInfo.InvariantCulture);
}
