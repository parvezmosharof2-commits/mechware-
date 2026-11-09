namespace Mechware.Infrastructure.Persistence;

/// <summary>
/// Tamper-evident audit log (threat model v1, H3; brief §6.11). Rows are written by the app as before; a worker then
/// <b>seals</b> them into a hash chain: each sealed row stores the previous row's hash and a SHA-256 over that hash and its
/// own content (canonical JSON, so formatting cannot differ). Changing, removing or reordering a sealed row breaks every
/// hash after it, which <c>verify_audit_chain()</c> reports. A trigger makes the table append-only for the application
/// role, and the worker logs the chain head on every run, so a copy of the head lives outside the database (the anchor).
/// <para>
/// The SQL is idempotent. Migrations run it on real databases; tests that build their schema with EnsureCreated run it too.
/// </para>
/// </summary>
public static class AuditChainSql
{
    public const string Install = """
        ALTER TABLE audit_logs ADD COLUMN IF NOT EXISTS chain_position bigint NULL;
        ALTER TABLE audit_logs ADD COLUMN IF NOT EXISTS prev_hash text NULL;
        ALTER TABLE audit_logs ADD COLUMN IF NOT EXISTS hash text NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_audit_logs_chain_position ON audit_logs (chain_position);
        CREATE INDEX IF NOT EXISTS ix_audit_logs_unsealed ON audit_logs (created_at, id) WHERE chain_position IS NULL;

        -- The content a hash covers. Timestamps as microseconds since the epoch, so the session time zone cannot change them.
        CREATE OR REPLACE FUNCTION audit_row_hash(prev text, a audit_logs) RETURNS text
        LANGUAGE sql IMMUTABLE AS $fn$
            SELECT encode(sha256(convert_to(jsonb_build_array(
                prev, a.id, a.actor_id, a.actor_ip, a.action, a.entity_name, a.entity_id,
                a.old_values, a.new_values, a.correlation_id,
                (extract(epoch FROM a.created_at) * 1000000)::bigint)::text, 'UTF8')), 'hex')
        $fn$;

        -- Append-only: no delete or truncate; an update may only seal a row once and touch nothing else.
        CREATE OR REPLACE FUNCTION audit_logs_append_only() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        BEGIN
            IF TG_OP IN ('DELETE', 'TRUNCATE') THEN
                RAISE EXCEPTION 'audit_logs is append-only' USING ERRCODE = 'insufficient_privilege';
            END IF;
            IF OLD.hash IS NOT NULL THEN
                RAISE EXCEPTION 'a sealed audit row cannot change' USING ERRCODE = 'insufficient_privilege';
            END IF;
            IF (NEW.id, NEW.actor_id, NEW.actor_ip, NEW.action, NEW.entity_name, NEW.entity_id, NEW.old_values,
                NEW.new_values, NEW.correlation_id, NEW.created_at)
               IS DISTINCT FROM
               (OLD.id, OLD.actor_id, OLD.actor_ip, OLD.action, OLD.entity_name, OLD.entity_id, OLD.old_values,
                OLD.new_values, OLD.correlation_id, OLD.created_at) THEN
                RAISE EXCEPTION 'audit rows cannot change' USING ERRCODE = 'insufficient_privilege';
            END IF;
            RETURN NEW;
        END
        $fn$;

        DROP TRIGGER IF EXISTS trg_audit_logs_append_only ON audit_logs;
        CREATE TRIGGER trg_audit_logs_append_only BEFORE UPDATE OR DELETE ON audit_logs
            FOR EACH ROW EXECUTE FUNCTION audit_logs_append_only();
        DROP TRIGGER IF EXISTS trg_audit_logs_no_truncate ON audit_logs;
        CREATE TRIGGER trg_audit_logs_no_truncate BEFORE TRUNCATE ON audit_logs
            FOR EACH STATEMENT EXECUTE FUNCTION audit_logs_append_only();

        -- Seals unsealed rows older than settle, oldest first. One sealer at a time (advisory lock), so positions are
        -- contiguous. A row that commits late is simply sealed in a later run: the chain order is the sealing order.
        CREATE OR REPLACE FUNCTION seal_audit_logs(max_rows integer, settle interval) RETURNS integer
        LANGUAGE plpgsql AS $fn$
        DECLARE
            r audit_logs;
            prev text;
            pos bigint;
            h text;
            n integer := 0;
        BEGIN
            PERFORM pg_advisory_xact_lock(hashtextextended('audit-chain', 0));
            SELECT a.hash, a.chain_position INTO prev, pos
              FROM audit_logs a WHERE a.chain_position IS NOT NULL ORDER BY a.chain_position DESC LIMIT 1;
            prev := coalesce(prev, 'genesis');
            pos := coalesce(pos, 0);
            FOR r IN SELECT * FROM audit_logs
                      WHERE chain_position IS NULL AND created_at <= now() - settle
                      ORDER BY created_at, id LIMIT max_rows FOR UPDATE
            LOOP
                pos := pos + 1;
                h := audit_row_hash(prev, r);
                UPDATE audit_logs SET chain_position = pos, prev_hash = prev, hash = h WHERE id = r.id;
                prev := h;
                n := n + 1;
            END LOOP;
            RETURN n;
        END
        $fn$;

        -- Recomputes the whole chain. first_broken_position is null when every sealed row is intact and in place.
        CREATE OR REPLACE FUNCTION verify_audit_chain()
            RETURNS TABLE (sealed bigint, first_broken_position bigint, head_hash text)
        LANGUAGE plpgsql AS $fn$
        DECLARE
            r audit_logs;
            prev text := 'genesis';
            expected bigint := 0;
            broken bigint := NULL;
        BEGIN
            FOR r IN SELECT * FROM audit_logs WHERE chain_position IS NOT NULL ORDER BY chain_position LOOP
                expected := expected + 1;
                IF broken IS NULL AND (r.chain_position <> expected OR r.prev_hash IS DISTINCT FROM prev
                                       OR r.hash IS DISTINCT FROM audit_row_hash(prev, r)) THEN
                    broken := expected;
                END IF;
                prev := r.hash;
            END LOOP;
            RETURN QUERY SELECT expected, broken, CASE WHEN expected = 0 THEN NULL ELSE prev END;
        END
        $fn$;
        """;
}
