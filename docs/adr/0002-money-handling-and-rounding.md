# ADR 0002: Money Representation, Rounding, and Currency Handling

## Status
Accepted

## Date
2026-10-29

## Context
Industrial commerce involves high-value transactions, multi-currency purchasing (BDT for domestic, USD for export), category-specific VAT rates, tiered promotional discounts, multi-seller commissions, and double-entry financial ledgers.
Using IEEE 754 floating-point numbers (`float` or `double`) leads to catastrophic precision loss (e.g. `0.1 + 0.2 = 0.30000000000000004`). Furthermore, naive rounding (such as round-half-up) introduces systemic upward bias across thousands of lines, creating discrepancies between invoiced totals, customer payments, and seller commission payouts.

## Decision
We enforce strict financial precision across the entire stack using three core mechanisms:

### 1. In-Memory Domain Representation: Value Object `Money`
In C# domain code (`ShopVerse.Domain`), all monetary figures are encapsulated by an immutable `Money` value object:
```csharp
public readonly record struct Money(decimal Amount, Currency Currency)
{
    // Native 128-bit decimal with exact fractional math
}
```
- The `Amount` is held in native 128-bit `decimal` with a scale of 2 decimal places (or 4 for intermediary fee calculations before line-level rounding).
- Currency is strictly validated against ISO 4217 (`BDT`, `USD`, `EUR`). Operations between mismatched currencies throw `CurrencyMismatchException`.

### 2. Database Storage: Minor Units as 64-Bit Integers
In PostgreSQL (`ShopVerse.Infrastructure`), money is stored as an integer representing the smallest currency minor unit:
- **BDT / USD / EUR**: Stored as `bigint` (Poisha / Cents). For example, 1,250.50 BDT is stored as `125050`.
- Each monetary column is accompanied by a currency column:
  - `amount_minor bigint NOT NULL`
  - `currency varchar(3) NOT NULL DEFAULT 'BDT'`
- This guarantees zero floating-point drift, deterministic indexing, and compliance with high-volume database auditing standards.

### 3. Banker's Rounding (MidpointRounding.ToEven)
- All line-item discount distributions, VAT computations, and commission splits apply **Banker's Rounding (`MidpointRounding.ToEven`)**:
  - Halves round to the nearest even number (e.g., 2.5 rounds to 2, 3.5 rounds to 4).
  - This eliminates statistical bias over large volumes of ledger transactions.
- Rounding occurs at the **line-item level**, not on individual unit fractions. The sum of item totals + tax + shipping - discounts is guaranteed to match the grand total:
  $$\text{GrandTotal} = \sum \text{LineTotals} + \text{ShippingTotal} + \text{TaxTotal} - \text{DiscountTotal}$$

### 4. Client Communication (JSON API)
API endpoints expose monetary objects with both human-readable decimal strings and minor unit integers to prevent client parsing errors:
```json
{
  "amount": "1250.50",
  "amountMinor": 125050,
  "currency": "BDT",
  "formatted": "৳1,250.50"
}
```

## Consequences
### Positive:
- Absolute financial accuracy with zero rounding drift across millions of transactions.
- Invariant validation in domain constructors prevents negative amounts where forbidden.
- Complete auditability and seamless double-entry ledger balance (`Debit == Credit`).

### Negative:
- Conversion between minor units and decimal must be explicitly handled in EF Core Value Converters.
- Developers must never perform direct mathematical operations on raw integers without the `Money` value object.
