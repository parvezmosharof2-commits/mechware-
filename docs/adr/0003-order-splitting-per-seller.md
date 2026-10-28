# ADR 0003: Order Splitting per Seller (Parent Order and SubOrder Hierarchy)

## Status
Accepted

## Date
2026-10-29

## Context
MECHWARE operates as a hybrid direct manufacturer and multi-vendor industrial marketplace. A single customer cart may contain:
1. Made-in-Bangladesh CNC machinery sold directly by MECHWARE.
2. Siemens S7-1500 PLCs sold by an authorized distributor in Dhaka.
3. Pneumatic valves and sensors sold by a third-party seller in Chittagong.

The customer expects a unified shopping cart, a single unified payment transaction (e.g., via bKash or corporate credit card), and a consolidated master order number (`SV-YYMMDD-XXXXXX`). However:
- Each seller fulfills items from their own warehouse or factory.
- Couriers (Steadfast, Pathao, Paperfly) assign distinct tracking numbers and shipping labels per pickup location.
- Seller commissions, payouts, cancellations, and return requests must be isolated per seller.
- Delivery timelines differ between direct custom machinery (built to order) and off-the-shelf automation components.

## Decision
We implement a **Two-Tier Order Hierarchy**:

```
[ Parent Order (Order) ]  -- 1 per Customer Checkout Aggregate
       |
       +---> [ SubOrder 1 ] (Seller: MechWare Direct) -> [ Shipment 1 ] -> Items
       |
       +---> [ SubOrder 2 ] (Seller: Alpha Automation) -> [ Shipment 2 ] -> Items
```

### 1. Parent Order (`orders`)
- **Role**: Customer-facing aggregate root and commercial contract.
- **Attributes**: `id`, `order_number` (format `SV-YYMMDD-XXXXXX`), `user_id` (or guest credentials), `currency`, `subtotal`, `discount_total`, `shipping_total`, `tax_total`, `grand_total`, `payment_status` (`unpaid`, `paid`, `partially_refunded`, `refunded`), snapshot addresses, and customer notes.
- **Payment Association**: Exactly one parent payment transaction covers the entire parent order.

### 2. SubOrder (`sub_orders`)
- **Role**: Operational fulfillment and financial settlement unit per seller.
- **Attributes**: `id`, `order_id`, `seller_id`, `sub_order_number` (`SV-YYMMDD-XXXXXX-01`), `status`, `subtotal`, `shipping_fee`, `commission_rate`, `commission_amount`, `seller_earning`, `shipment_id`.
- **State Machine**: Managed independently per `SubOrder`:
  $$\text{PendingPayment} \rightarrow \text{Confirmed} \rightarrow \text{Processing} \rightarrow \text{Packed} \rightarrow \text{Shipped} \rightarrow \text{OutForDelivery} \rightarrow \text{Delivered} \rightarrow \text{Completed}$$
  Side paths: `Cancelled`, `DeliveryFailed -> ReturnedToSeller`, `ReturnRequested -> ReturnApproved -> Refunded`.
- **Items**: `order_items` belong to a specific `sub_order_id`.

### 3. Lifecycle Rules:
- **Order Confirmation**: When payment webhook captures funds (or COD OTP verifies), the parent order transitions to `Paid`/`Confirmed`, and all child `SubOrders` transition to `Confirmed`.
- **Independent Cancellation**: If a customer cancels an item before packing, only the affected `SubOrder` is updated. If the entire `SubOrder` cancels, an automatic partial refund is issued to the customer.
- **Completion & Seller Payout**: A `SubOrder` transitions to `Completed` when `Delivered` status has persisted past the return window (default 7 days). Only upon reaching `Completed` are the seller earnings released to the payable balance in the double-entry financial ledger.

## Consequences
### Positive:
- Perfect alignment with real-world multi-vendor warehouse logistics and courier pickup realities.
- Complete seller isolation: Sellers can only view, pack, and ship their own `SubOrders`.
- Clear refund, commission, and return calculations without cross-vendor contamination.

### Negative:
- Slightly increased data normalization and multi-table queries on customer order tracking pages (mitigated by read-optimized projections and Dapper read models).
