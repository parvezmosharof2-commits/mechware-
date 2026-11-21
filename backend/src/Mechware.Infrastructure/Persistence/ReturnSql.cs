namespace Mechware.Infrastructure.Persistence;

/// <summary>
/// The database backstop of the F2.1 quantity rule: a return line names a line of its own request's order, and all lines of open
/// requests (anything but withdrawn or rejected) together never ask more than that order line's quantity (= delivered while an order has
/// one shipment). The order line row is locked first, so even a writer that skipped the service's order lock cannot over-claim in a
/// race. A withdrawn or rejected request never opens again (it would claim its freed units back unchecked). Idempotent: the migration
/// runs it, and tests that build their schema with EnsureCreated run it too.
/// </summary>
public static class ReturnSql
{
    public const string Install = """
        CREATE OR REPLACE FUNCTION return_request_lines_guard() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        DECLARE
            request_order uuid;
            item_order uuid;
            ordered int;
            claimed int;
        BEGIN
            SELECT r.order_id INTO request_order FROM return_requests r WHERE r.id = NEW.return_request_id;
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

        CREATE OR REPLACE FUNCTION return_requests_no_reopen() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        BEGIN
            IF OLD.status IN ('Withdrawn', 'Rejected') AND NEW.status <> OLD.status THEN
                RAISE EXCEPTION 'a % return request cannot change status', OLD.status USING ERRCODE = 'check_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;

        DROP TRIGGER IF EXISTS trg_return_requests_no_reopen ON return_requests;
        CREATE TRIGGER trg_return_requests_no_reopen BEFORE UPDATE OF status ON return_requests
            FOR EACH ROW EXECUTE FUNCTION return_requests_no_reopen();
        """;

    public const string Uninstall = """
        DROP TRIGGER IF EXISTS trg_return_requests_no_reopen ON return_requests;
        DROP FUNCTION IF EXISTS return_requests_no_reopen();
        DROP TRIGGER IF EXISTS trg_return_request_lines_guard ON return_request_lines;
        DROP FUNCTION IF EXISTS return_request_lines_guard();
        """;
}
