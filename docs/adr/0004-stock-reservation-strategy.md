# ADR 0004: Stock Reservation Strategy and High-Concurrency Inventory Control

## Status
Accepted

## Date
2026-10-29

## Context
Industrial automation components (e.g., specialized Siemens S7-1500 CPUs, Omron optical sensors, precision servo drives) have limited stock and high unit costs. Overselling damages customer trust and operational credibility.
During flash sales, new industrial product launches, or high-traffic promotional campaigns, up to 5,000 concurrent buyers may attempt to purchase scarce inventory (e.g., 50 parallel buyers for 10 available units).
A naive checkout flow that decrements stock only after payment confirmation results in severe overselling. Conversely, decrementing stock upon adding to cart locks inventory prematurely, enabling malicious cart hoarding.

## Decision
We implement a **Two-Phase Inventory Reservation Pattern** with tiered concurrency protection:

```
[ Customer Initiates Checkout ]
                 │
                 ▼
[ Redis RedLock (Distributed Lock per Variant) ]
                 │
                 ▼
[ PostgreSQL Row Lock: SELECT ... FOR UPDATE ]
                 │
   Available = on_hand - reserved
   IF Available >= requested_quantity:
        INSERT INTO stock_reservations (expires_at = NOW() + 15m)
        UPDATE inventory_items SET reserved = reserved + requested_quantity
                 │
                 ▼
[ Stock Reserved (15-Minute Reservation Token) ]
        ┌────────┴────────┐
        ▼                 ▼
[ Payment Captured ]  [ Payment Failed / Expired ]
        │                 │
  Convert to deduction:   Worker releases reservation:
  on_hand -= qty          reserved -= qty
  reserved -= qty         DELETE reservation
```

### 1. Invariants & Data Structure
- For every inventory record:
  $$\text{Available} = \text{OnHand} - \text{Reserved}$$
  $$\text{Constraint: } \text{Reserved} \ge 0 \quad \text{and} \quad \text{OnHand} \ge \text{Reserved}$$
- Stock reservation record:
  - `id (UUID)`
  - `variant_id (UUID)`
  - `warehouse_id (UUID)`
  - `cart_id (UUID)` / `order_id (UUID)`
  - `quantity (int)`
  - `expires_at (timestamptz)` (strictly 15 minutes from reservation initiation)

### 2. Concurrency Strategy
1. **Tier 1 — High-Contention Guard (Redis RedLock)**:
   For products under high write pressure, a distributed lock with key `lock:inventory:{variantId}` is acquired with a 2-second timeout.
2. **Tier 2 — ACID Database Concurrency & Deadlock Prevention**:
   Inside an EF Core / Npgsql database transaction, all inventory items for a multi-line order are locked in **strict deterministic UUID order (`ORDER BY variant_id ASC, warehouse_id ASC`)** to mathematically prevent AB-BA deadlocks between concurrent checkouts:
   ```sql
   SELECT id, variant_id, warehouse_id, on_hand, reserved 
   FROM inventory_items 
   WHERE (variant_id, warehouse_id) IN ((@v1, @w1), (@v2, @w2))
   ORDER BY variant_id ASC, warehouse_id ASC
   FOR UPDATE;
   ```
   If `(on_hand - reserved) < requested_quantity` for any item, the transaction rolls back immediately and returns an RFC 7807 `INSUFFICIENT_STOCK` error.
   If all items are sufficient, `reserved` is incremented, and corresponding `stock_reservations` records are created with `status = 'active'`.

### 3. Expiry and Reconciliation
- A background worker (`ShopVerse.Worker`) runs every 60 seconds executing an atomic set-based CTE query with `FOR UPDATE SKIP LOCKED` to release expired active holds without lock contention:
  ```sql
  WITH expired_holds AS (
      SELECT id, variant_id, warehouse_id, quantity
      FROM stock_reservations
      WHERE status = 'active' AND expires_at <= NOW()
      ORDER BY variant_id ASC, warehouse_id ASC
      FOR UPDATE SKIP LOCKED
      LIMIT 500
  ),
  released_reservations AS (
      UPDATE stock_reservations sr
      SET status = 'expired'
      FROM expired_holds eh
      WHERE sr.id = eh.id
      RETURNING eh.variant_id, eh.warehouse_id, eh.quantity
  )
  UPDATE inventory_items inv
  SET reserved = inv.reserved - agg.total_qty,
      updated_at = NOW()
  FROM (
      SELECT variant_id, warehouse_id, SUM(quantity) AS total_qty
      FROM released_reservations
      GROUP BY variant_id, warehouse_id
  ) agg
  WHERE inv.variant_id = agg.variant_id
    AND inv.warehouse_id = agg.warehouse_id;
  ```
- When an order payment is captured, the reservation is transitioned to `status = 'fulfilled'`, atomically decrementing `on_hand` and `reserved`.

## Consequences
### Positive:
- **Zero Overselling**: Mathematically impossible for concurrent checkouts to exceed available inventory.
- **Fair Access**: True 15-minute purchasing window for buyers without locking stock indefinitely.
- **Resilient**: If the customer abandons checkout, the background worker recycles the stock seamlessly without human intervention.

### Negative:
- Database row-level locking introduces minor serialization queues under extreme load (tested up to 5,000 concurrent checkouts via k6).
