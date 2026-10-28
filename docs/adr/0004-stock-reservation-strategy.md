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
   For products under high write pressure, a distributed lock with a key `lock:inventory:{variantId}` is acquired with a 2-second timeout.
2. **Tier 2 — ACID Database Concurrency**:
   Inside an EF Core / Npgsql database transaction, the target row in `inventory_items` is selected with `FOR UPDATE`:
   ```sql
   SELECT id, on_hand, reserved 
   FROM inventory_items 
   WHERE variant_id = @variantId AND warehouse_id = @warehouseId 
   FOR UPDATE;
   ```
   If `(on_hand - reserved) < requested_quantity`, the transaction rolls back immediately and returns an RFC 7807 `INSUFFICIENT_STOCK` error.
   If sufficient, `reserved` is incremented and a `stock_reservations` row is created.

### 3. Expiry and Reconciliation
- A background worker (`ShopVerse.Worker`) runs every 60 seconds executing:
  ```sql
  SELECT * FROM stock_reservations WHERE expires_at < NOW() AND status = 'pending';
  ```
  Expired reservations are atomatically cancelled in a batch transaction, decrementing `reserved` on the inventory items and emitting a `StockReservationExpiredDomainEvent`.
- When an order payment is captured, the reservation is marked as `Fulfilled`, decrementing `on_hand` and `reserved` atomically.

## Consequences
### Positive:
- **Zero Overselling**: Mathematically impossible for concurrent checkouts to exceed available inventory.
- **Fair Access**: True 15-minute purchasing window for buyers without locking stock indefinitely.
- **Resilient**: If the customer abandons checkout, the background worker recycles the stock seamlessly without human intervention.

### Negative:
- Database row-level locking introduces minor serialization queues under extreme load (tested up to 5,000 concurrent checkouts via k6).
