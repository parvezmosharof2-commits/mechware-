namespace Mechware.Infrastructure.Persistence;

/// <summary>
/// The database backstop of the return rules. F2.1: a return line names a line of its own request's order, and all lines of open
/// requests (anything but withdrawn or rejected) together never ask more than that order line's quantity (= delivered while an order has
/// one shipment); the order line row is locked first, so even a writer that skipped the service's order lock cannot over-claim in a race.
/// F2.3a: a line keeps its order line and quantity, is inspected once and only while its return is received; the status moves only along
/// Requested → Approved | Rejected | Withdrawn, Approved → Received → Inspected → Refunded | CreditNoted (so a withdrawn or rejected request
/// never opens again and claims its freed units back); at commit an inspected return has every line inspected and no other has any.
/// Idempotent: the migrations run it, and tests that build their schema with EnsureCreated run it too.
/// </summary>
public static class ReturnSql
{
    public const string Install = """
        CREATE OR REPLACE FUNCTION return_request_lines_guard() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        DECLARE
            request_order uuid;
            request_status text;
            item_order uuid;
            ordered int;
            claimed int;
        BEGIN
            SELECT r.order_id, r.status INTO request_order, request_status FROM return_requests r WHERE r.id = NEW.return_request_id;
            IF TG_OP = 'UPDATE' THEN
                IF NEW.return_request_id <> OLD.return_request_id OR NEW.order_item_id <> OLD.order_item_id OR NEW.quantity <> OLD.quantity THEN
                    RAISE EXCEPTION 'a return line keeps its order line and quantity' USING ERRCODE = 'check_violation';
                END IF;
                IF OLD.accepted_quantity IS NOT NULL AND (NEW.accepted_quantity, NEW.restock_quantity, NEW.inspection_note)
                        IS DISTINCT FROM (OLD.accepted_quantity, OLD.restock_quantity, OLD.inspection_note) THEN
                    RAISE EXCEPTION 'an inspected return line cannot change' USING ERRCODE = 'check_violation';
                END IF;
                IF NEW.accepted_quantity IS NOT NULL AND OLD.accepted_quantity IS NULL AND request_status NOT IN ('Received', 'Inspected') THEN
                    RAISE EXCEPTION 'only a received return is inspected' USING ERRCODE = 'check_violation';
                END IF;
                RETURN NEW;   -- the quantity did not change, so neither did the claims
            END IF;

            IF request_status <> 'Requested' OR NEW.accepted_quantity IS NOT NULL THEN
                RAISE EXCEPTION 'lines are added only to a new return request' USING ERRCODE = 'check_violation';
            END IF;
            SELECT s.order_id, i.quantity INTO item_order, ordered
              FROM order_items i JOIN sub_orders s ON s.id = i.sub_order_id
             WHERE i.id = NEW.order_item_id FOR UPDATE OF i;
            IF item_order IS DISTINCT FROM request_order THEN
                RAISE EXCEPTION 'return line % is not a line of the returned order', NEW.order_item_id USING ERRCODE = 'check_violation';
            END IF;
            SELECT COALESCE(SUM(l.quantity), 0) INTO claimed
              FROM return_request_lines l JOIN return_requests r ON r.id = l.return_request_id
             WHERE l.order_item_id = NEW.order_item_id AND l.id <> NEW.id AND r.status NOT IN ('Withdrawn', 'Rejected');
            IF claimed + NEW.quantity > ordered THEN
                RAISE EXCEPTION 'return of order line % exceeds the delivered quantity', NEW.order_item_id USING ERRCODE = 'check_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;

        DROP TRIGGER IF EXISTS trg_return_request_lines_guard ON return_request_lines;
        CREATE TRIGGER trg_return_request_lines_guard BEFORE INSERT OR UPDATE ON return_request_lines
            FOR EACH ROW EXECUTE FUNCTION return_request_lines_guard();

        DROP TRIGGER IF EXISTS trg_return_requests_no_reopen ON return_requests;
        DROP FUNCTION IF EXISTS return_requests_no_reopen();
        CREATE OR REPLACE FUNCTION return_requests_transition() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        BEGIN
            IF NEW.status <> OLD.status AND (OLD.status::text, NEW.status::text) NOT IN (
                ('Requested', 'Approved'), ('Requested', 'Rejected'), ('Requested', 'Withdrawn'), ('Approved', 'Received'),
                ('Received', 'Inspected'), ('Inspected', 'Refunded'), ('Inspected', 'CreditNoted')) THEN
                RAISE EXCEPTION 'a return request cannot move from % to %', OLD.status, NEW.status USING ERRCODE = 'check_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;

        DROP TRIGGER IF EXISTS trg_return_requests_transition ON return_requests;
        CREATE TRIGGER trg_return_requests_transition BEFORE UPDATE OF status ON return_requests
            FOR EACH ROW EXECUTE FUNCTION return_requests_transition();

        CREATE OR REPLACE FUNCTION return_requests_inspected_lines() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        DECLARE
            request_id uuid;
            request_status text;
            open_lines int;
            inspected_lines int;
        BEGIN
            IF TG_TABLE_NAME = 'return_requests' THEN
                request_id := NEW.id;
            ELSE
                request_id := NEW.return_request_id;
            END IF;
            SELECT r.status INTO request_status FROM return_requests r WHERE r.id = request_id;
            SELECT count(*) FILTER (WHERE accepted_quantity IS NULL), count(*) FILTER (WHERE accepted_quantity IS NOT NULL)
              INTO open_lines, inspected_lines FROM return_request_lines WHERE return_request_id = request_id;
            IF request_status IN ('Inspected', 'Refunded', 'CreditNoted') AND open_lines > 0 THEN
                RAISE EXCEPTION 'an inspected return has every line inspected' USING ERRCODE = 'check_violation';
            END IF;
            IF request_status NOT IN ('Inspected', 'Refunded', 'CreditNoted') AND inspected_lines > 0 THEN
                RAISE EXCEPTION 'only an inspected return has inspected lines' USING ERRCODE = 'check_violation';
            END IF;
            RETURN NULL;
        END
        $fn$;

        DROP TRIGGER IF EXISTS trg_return_requests_inspected_lines ON return_requests;
        CREATE CONSTRAINT TRIGGER trg_return_requests_inspected_lines AFTER INSERT OR UPDATE ON return_requests
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION return_requests_inspected_lines();
        DROP TRIGGER IF EXISTS trg_return_request_lines_inspected ON return_request_lines;
        CREATE CONSTRAINT TRIGGER trg_return_request_lines_inspected AFTER UPDATE ON return_request_lines
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION return_requests_inspected_lines();
        """;

    /// <summary>Removes every return trigger and function, of F2.1 and of F2.3a.</summary>
    public const string Uninstall = """
        DROP TRIGGER IF EXISTS trg_return_request_lines_inspected ON return_request_lines;
        DROP TRIGGER IF EXISTS trg_return_requests_inspected_lines ON return_requests;
        DROP FUNCTION IF EXISTS return_requests_inspected_lines();
        DROP TRIGGER IF EXISTS trg_return_requests_transition ON return_requests;
        DROP FUNCTION IF EXISTS return_requests_transition();
        DROP TRIGGER IF EXISTS trg_return_requests_no_reopen ON return_requests;
        DROP FUNCTION IF EXISTS return_requests_no_reopen();
        DROP TRIGGER IF EXISTS trg_return_request_lines_guard ON return_request_lines;
        DROP FUNCTION IF EXISTS return_request_lines_guard();
        """;
}
