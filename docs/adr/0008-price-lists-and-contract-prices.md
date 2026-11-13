# ADR 0008: Price Lists, Contract Prices and Price Resolution

## Status
Accepted 2026-11-13. The owner delegated the §12 decisions to the main session ("tomar jeta valo money hoi koro"); the
choices are recorded in §12 under **Decided**. The margin-floor numbers are a conservative default and are data, not
code: the owner can change them at any time (D2.9) without a release.

## Date
2026-11-13

## Context
Brief §8.1 asks for a server-side price per buyer, variant and quantity, taken from: accepted quote line, company
contract price, customer-group price list, quantity break on list price, list price. It also says: sale prices apply
only to list price, the chosen source is returned to the UI, and price-list changes need a second staff approver.

What exists today (D1, E2):
- `PricingEngine` gives the list or sale price of a variant in a tax mode; `OrderPricing` does the line math (one
  banker's rounding per line, ADR-0002); `PromotionEngine` applies coupons and automatic promotions.
- `OrderService.PriceAsync` prices every line from `ProductVariant` alone. The client sends only variant id and quantity.
- `ProductVariant.CostPriceMinor` exists. `Company.DefaultPriceListId` exists as a column; no code writes it.
- Four-eyes exists for refunds (`Refund.Approve`), step-up exists as `[RequireStepUp("…")]` (header `X-Step-Up-Code`).
- Threat model v2, rule R3, already fixed six rules for company price lists. This ADR implements them.

The D2 card contradicted itself: its boundary line read the brief as a precedence order ("quote > contract > group >
break > list"), its D2.2 exit test said "the resolver always picks the lowest valid source". §5 settles this.

## Decision

### 1. Model

**PriceList** (one row per version)

| Field | Rule |
|---|---|
| `id`, `seriesId`, `version` | A series is one list over time; versions are 1, 2, 3 …; `(seriesId, version)` is unique. |
| `name` | For staff. |
| `scope` | `General` (every buyer: public quantity breaks) · `Group` (shared list, assigned to companies) · `Company` (contract). Cannot change after creation. |
| `companyId` | Required for `Company`, forbidden otherwise. Cannot change after creation. |
| `contractRef` | Required for `Company` (the contract or agreement number, ≤ 64 characters). Shown to the buyer and printed later. |
| `currency` | `BDT` (§7). |
| `validFromUtc`, `validToUtc` | Window `[from, to)`, the same convention as D1 sale windows. `validToUtc` is required; the window is at most `MaxValidityDays`. |
| `status` (stored) | `Draft` → `PendingApproval` → `Approved` → `Withdrawn`. A rejection returns the list to `Draft` with a comment. |
| `createdBy`, `submittedBy`, `submittedAtUtc`, `contentHash` | `contentHash` = SHA-256 of the header and all entries, computed by the server at submit. |
| `approvedBy`, `approvedAtUtc`, `effectiveFromUtc` | Set by the approval (§3, §10). |
| `withdrawnBy`, `withdrawnAtUtc`, `withdrawReason` | Set by a withdrawal. |
| contributors | Every user who created or changed this version (table `price_list_contributors`). |

**A contract price is not a separate thing.** It is an entry of a `Company` list. A `Group` list is a shared list;
"the group" is the set of companies whose `Company.DefaultPriceListId` points at its series. No customer-group table
is needed.

What staff and buyers see as status is **computed**, not stored: an `Approved` list is *Scheduled* before
`effectiveFromUtc`, *Active* inside the window, *Expired* after `validToUtc`, *Superseded* once a higher version of
the series has started. Nothing has to flip a status at midnight, so no worker can be late and no order
can race with one.

**PriceListEntry**

| Field | Rule |
|---|---|
| `variantId` | Must exist; the variant's currency must equal the list's currency. |
| `minQty` | 1 … 10,000 (`PlaceOrderRequest.MaxQuantityPerLine`). `General` lists accept only `minQty ≥ 2`: the price for one unit is the catalogue's job. |
| `unitPriceMinor` | Whole poisha, `> 0`, `≤ MaxUnitPriceMinor`. An absolute price, never "x % off". |
| `belowFloorReason` | Required when the entry is below the margin floor (§4). |
| `costMinorAtApproval`, `floorMinorAtApproval`, `belowFloor` | Written by the approval, for the audit. Staff with `pricing.view_cost` only. |

`(priceListId, variantId, minQty)` is unique. **Quantity breaks** are simply several entries of one variant with
different `minQty`. Within one list the price of a variant must not rise as `minQty` grows. At most
`MaxEntriesPerList` entries.

**MarginFloor** (data, not code): `scope` (`Global`, `Category`, `Brand`, `BrandAndCategory`), `brandId?`,
`categoryId?`, `minMarkupPercent`. See §4.

**Options** (section `Pricing`, validated at start): `AllowedCurrencies` = `["BDT"]`, `MaxValidityDays` = 366,
`ActivationDelaySeconds` = 60, `MaxEntriesPerList` = 5,000, `MaxUnitPriceMinor` = 100,000,000,000
(৳1,000,000,000.00), `AllowBelowCost` = false.

**Permissions** (new): `pricelists.view`, `pricelists.edit` (create, change, submit, revise), `pricelists.approve`
(approve, reject, withdraw), `pricelists.approve_below_floor`, `pricing.view_cost`, `pricing.manage_floors`.
Proposed defaults: Admin all; Finance `view`, `approve`, `view_cost`; Support `view`, `edit`. The last two
permissions stay with Admin.

### 2. Tax basis: negotiated prices are VAT-exclusive
Entries of `Company` and `Group` lists (and D4 quote lines) are **VAT-exclusive**. Reasons:
1. D1 already prices company orders in `TaxMode.Exclusive`: `OrderLineDraft.UnitPriceMinor` is the net price and
   `OrderPricing` adds VAT per line. A contract entry goes into the line unchanged, with no conversion and no rounding.
2. Brief §8.1: companies see VAT-exclusive prices and a VAT line. Mushak-6.3 prints the unit price without VAT.
3. If NBR or the category's VAT rate changes, the signed net price stays what was signed and only the VAT line
   moves. A VAT-inclusive entry would quietly change MECHWARE's revenue instead.
4. Negotiated prices never apply to guests or personal orders (those are `TaxMode.Inclusive`), so they never need a
   second basis.

Entries of `General` lists use the **same basis as the catalogue price** of the variant, because guests and
companies both see them; they go through `PricingEngine` exactly like the list price.

Before comparing, the resolver brings every candidate to the basis of the order's tax mode (net for a company).

**Finding (exists today, not caused by D2) → owner decision 2 in §12.** One stored catalogue price has two meanings.
For a guest it is read as VAT-inclusive (`TaxMode.Inclusive`); for a company the same number is read as net and VAT
is added on top (`OrderService.cs`, mode chosen from `CompanyId`; `OrderServiceTests` asserts 105,000 for a
1,000.00 price: "5 % is added to the 1,000.00 price"). So today a company pays 1,050.00 for an item a guest buys
for 1,000.00. "Lowest valid" and "Your contract price" mean little until this is one number with one meaning.

### 3. Lifecycle and four-eyes approval
- **Draft:** editable by `pricelists.edit`. Every change records the user as a contributor. The create and edit
  requests carry no status, approver or author fields.
- **Submit:** validates everything (§1, §4), stores `contentHash`, freezes the list. A pending list cannot be
  edited; "return to draft" or a rejection (comment required) unfreezes it and a new submit is needed.
- **Approve** needs all of:
  1. permission `pricelists.approve`;
  2. a fresh authenticator code: `[RequireStepUp("pricelist.approve")]`;
  3. **the approver is not the creator, not the submitter and not any contributor of this version**;
  4. the `contentHash` the approver was shown, sent back in the request; a different hash → `409 PRICE_LIST_CHANGED`;
  5. the margin-floor check of §4, run again inside the approval transaction;
  6. the list row locked `FOR UPDATE` and still `PendingApproval` (two approvals at once → one wins, one gets 409).
- **Approved lists are immutable.** A change is a new version: "revise" copies the latest approved version into
  Draft `version + 1`. One open (Draft or Pending) version per series.
- **Withdraw** (emergency stop for a wrong approval): `pricelists.approve` + step-up + reason. One person is enough,
  because a withdrawal can never lower a price: a version withdrawn after it started keeps superseding the older
  versions, so the buyer falls back to the other sources (normally the public price), not to an older, cheaper
  version. Bringing old prices back is a new version and needs two people. A version withdrawn before it started
  simply never starts. Ending a contract early = withdraw its current version and any scheduled one.
- **Database backstops** (a code bug must not be enough): `CHECK (approved_by <> created_by AND approved_by <>
  submitted_by)`, `CHECK (unit_price_minor > 0)`, `CHECK (min_qty >= 1)`, and a trigger that refuses any `UPDATE`
  or `DELETE` of an entry, and any header change other than the withdrawal, once a list is `Approved` or `Withdrawn`.
- **Audit:** create, edit, submit, approve, reject, withdraw, revise and every floor change go to the audit log with
  actor, IP and `contentHash`. Approved and withdrawn lists are never deleted (order lines point at them).

### 4. Margin floor
- **Definition:** the floor is a minimum markup on cost.
  `floorMinor = ceil(costMinor × (100 + minMarkupPercent) / 100)`. Rounded **up**, so rounding never lets a price
  under the floor. Cost is `ProductVariant.CostPriceMinor`, read as VAT-exclusive; the entry is compared on its net
  amount (a `General` entry is first divided by 1 + VAT).
- **Which floor:** the most specific row wins: brand and category → brand → category (the product's own, then the
  nearest ancestor) → global.
- **Fail closed:** a variant without a cost price, or no global floor row at all, makes the entry *below floor*.
- **Below floor:** allowed only with a written reason on the entry, an approver who also holds
  `pricelists.approve_below_floor`, and an explicit acknowledgement: the approve request lists the below-floor entry
  ids and they must equal the server's own list, otherwise `409 BELOW_FLOOR_NOT_ACKNOWLEDGED`.
- **Below cost:** refused at submit and at approval while `Pricing:AllowBelowCost` is false.
- **Checked twice:** at submit (so the author sees it) and again at approval with the cost and floor of that moment;
  the numbers used are stored on the entry.
- **Who sees it:** cost and floor amounts only with `pricing.view_cost`. No buyer-facing response has a cost,
  floor or margin field.
- **Changing a floor** is its own endpoint: `pricing.manage_floors` + step-up + audit with old and new value.
- The values are data in table `margin_floors`. **The owner must give them** (§12).

### 5. Resolution algorithm

```
Resolve(variant, quantity, buyer, asOfUtc) → unit price, source, list id, version, entry id, contractRef, public price

quantity = all units of this variant in the order (two lines of one variant are added up first)

public candidates (every buyer; put on the order's tax basis by PricingEngine):
  List            the variant's price
  Sale            the variant's sale price, only while PricingEngine.IsSaleActive(asOf)
  QuantityBreak   the entry with the largest minQty ≤ quantity of each applicable General list

negotiated candidates (only when the order is for a company AND the caller proved membership from company_members):
  GroupList       the entry with the largest minQty ≤ quantity of the applicable version of Company.DefaultPriceListId
  Contract        the entry with the largest minQty ≤ quantity of each applicable list with companyId = that company

a list version is applicable at asOf when
  status = Approved  AND  effectiveFromUtc ≤ asOf < validToUtc  AND  currency = order currency
  AND no higher version of the same series has started (effectiveFromUtc ≤ asOf), where "started" counts a version
      that is Approved, or that was Withdrawn after its effectiveFromUtc (§3)

choose (rule A, recommended):
  the lowest unit price of all candidates
  equal prices → the more specific source: Contract > GroupList > QuantityBreak > Sale > List
  still equal  → the later approvedAtUtc, then the smaller list id
```

An order made by **accepting a quote** (D4) is not resolved at all. Each line takes the quote version's snapshot
price with source `Quote`, even if something else is lower at that moment: brief §8.2 fixes quote prices for the
validity. A buyer who sees a lower public price simply orders normally instead of accepting.

The resolver is pure (domain, no I/O). The adapter `IPriceResolver` loads the candidates in one query for all lines
and is the only thing `OrderService`, the buyer price endpoint and D4's quote editor call:

```csharp
public enum PriceSource { List = 1, Sale = 2, QuantityBreak = 3, GroupList = 4, Contract = 5, Quote = 6 }
public sealed record PriceRequestLine(Guid VariantId, int Quantity);
public sealed record ResolvedPrice(Guid VariantId, long UnitPriceMinor, PriceSource Source, Guid? PriceListId,
    int? PriceListVersion, Guid? EntryId, string? ContractRef, long PublicUnitPriceMinor);
public interface IPriceResolver
{
    Task<IReadOnlyList<ResolvedPrice>> ResolveAsync(Guid? verifiedCompanyId, IReadOnlyList<PriceRequestLine> lines,
        TaxMode mode, DateTime asOfUtc, CancellationToken ct = default);
}
```

`verifiedCompanyId` must come from the membership check of the same request (B1 rule), never straight from the
request body. No buyer endpoint accepts a price-list id.

#### 5.1 OWNER DECISION 1 — "lowest valid wins" or "the most specific source wins"

| | **A — lowest valid wins (recommended)** | **B — precedence (contract, then group, then public)** |
|---|---|---|
| Rule | The buyer pays the lowest of all valid sources. The order contract > group > break > sale > list only breaks ties and names the source. | A contract price applies whenever one exists, even when the public price or a sale is lower. |
| Says so | Brief §8.1, in bold: "the unit price is the **lowest valid** of …"; the card's D2.2 exit test. | The card's boundary line; the usual ERP convention. |
| For the buyer | A signed-in company never pays more than a guest. | A company can see "Your contract price ৳1,000" next to a public sale price of ৳900. |
| For MECHWARE | Nobody has to re-check every contract when a list price drops or a sale starts. A contract price cannot be set above the public price (it would never apply). Sale and clearance prices also reach contract customers who buy on credit. | Contract prices above list are possible (for example to include service or the cost of credit). Staff must keep every contract in step with list-price drops; a report "contract entries above the public price" is needed. Buyers will order from a personal account to get the lower price, which bypasses company credit, approvals and the BIN on the invoice. |
| Invoice | The unit price can be lower than the signed contract rate, with source "Sale". | The unit price always equals the signed rate. |

**Recommendation: A.** The storefront shows prices to everyone, so with B the buyer can see that the contract costs
them money, and the team is too small to police contract lists by hand. A is also what the brief says. With A the
card's boundary sentence is corrected (done in the D2 card); with B the D2.2 property test becomes
`Resolver_ReturnsTheMostSpecificValidSource`. The rule is fixed in code, not a runtime setting; switching later
changes one function (`Choose`) and its property test. A mix (precedence, but never above the public price) differs
from A only when a group price beats a contract price and is not worth a third rule.

Two limits of A, on purpose: quotes are outside it (above), and it compares **unit prices before promotions** (§6).

### 6. Sale prices and promotions
- **Sale prices** (D1 sale windows) replace the list price only. They are never a percentage taken off a contract,
  group or quote price. Under rule A an active sale price is one more candidate and wins if it is the lowest.
- **Promotions do not stack on negotiated prices.** Lines whose source is `Contract`, `GroupList` or `Quote` are not
  handed to `PromotionEngine`; lines with a public source take coupons and automatic promotions as today, and
  minimum-spend thresholds count only those lines. A coupon with no eligible line gets the existing
  `NO_ELIGIBLE_LINES` answer. Reason: a coupon on top of a contract price would push the line under a floor that two
  people approved, with nobody approving the result.
- Known limit: the resolver compares unit prices; it does not try "list price with this coupon" against "contract
  price". A public price with a coupon can therefore end lower than a contract price.
- Staff should not give one variant both a `General` quantity break and an automatic `VolumeTier` promotion (both
  would apply). The console editor warns.
- Gap outside D2: sale prices and promotions are not checked against the margin floor today. `MarginFloorPolicy`
  is written so the catalogue and D3 can call it later.

### 7. Rounding and currency
- Every source is a **unit price in whole poisha**. Entries are absolute amounts, so D2 itself rounds nothing.
  The only derived unit price is a public price shown on the net basis (owner decision 2): it comes from
  `PricingEngine` (128-bit decimal, banker's rounding, once). Candidates are compared as integer poisha after that.
- The line is unchanged: `OrderPricing` multiplies quantity × unit price (`checked`), subtracts the discount and
  rounds VAT once per line (ADR-0002). No sub-poisha unit prices; very cheap items are sold by pack (`PackSize`).
- **BDT only.** A list's currency must be in `Pricing:AllowedCurrencies` and equal each entry's variant currency;
  the resolver ignores a list in another currency than the order. No exchange rates. USD lists need their own ADR.
- Validity is stored in UTC. The console collects Dhaka dates; "valid until 31 December" is stored as
  1 January 00:00 Asia/Dhaka, exclusive.

### 8. Snapshot on the order line
`order_items` gets: `price_source` (the enum above; existing rows = `List`), `price_list_id`, `price_list_version`,
`price_list_entry_id` (all nullable, foreign key `RESTRICT`), and `public_unit_price_minor` (what the public price
was at that instant, for "you save" and for audits). `unit_price_minor` and `vat_rate` are already snapshots.

After placement nothing reads a price list for that order again: order pages, approvals, payments and invoices
(E6, rule I4) use the order line. An order waiting for approval keeps the price it was placed with, also when the
list expires or is withdrawn meanwhile. An idempotent replay returns the original order, never a re-priced one.
`OrderItemDto` gains `PriceSource`.

### 9. A list expires between cart and checkout
- Cart and product pages show an **estimate**. The order is priced once, at one server instant (`asOf` = the clock
  reading `PlaceAsync` already takes), for all lines. No grace period: expired is expired.
- So that a higher price never arrives silently, `PlaceOrderRequest` gets an optional `ExpectedGrandTotalMinor`:
  the total the buyer was shown. The server prices the order itself as always; if its total is **higher** than the
  expected one it places nothing and answers `409 PRICE_CHANGED` with the new lines, and the buyer confirms again.
  A lower server total goes through. The field can only stop an order; it never changes a price.

### 10. Concurrency
- **Approval while orders are placed.** The approval sets
  `effectiveFromUtc = max(validFromUtc, approvedAtUtc + ActivationDelaySeconds)`. An order priced before that
  instant uses the old price, one priced at or after it the new one, and by then the approval is long committed.
  The price is therefore a function of `asOf` and committed data only: resolving again with the order's instant
  gives the same price and source. Backdating is impossible for the same reason (a `validFromUtc` in the past
  starts at the approval, not before), and an approval whose window has already ended is refused.
- **Withdrawal while orders are placed.** In the transaction that saves the order, the list rows the prices came
  from are read again `FOR SHARE`; if one is no longer `Approved` the order is not saved (`PRICE_CHANGED`). The
  withdrawal takes the row `FOR UPDATE`. So no order that commits after a withdrawal carries the withdrawn price.
- **Two approvals, or edit against submit:** row lock plus status check; optimistic concurrency token on drafts.

### 11. Reading prices: API and caches
- Staff: `api/v1/admin/price-lists` (list, create, change, submit, revise, approve, reject, withdraw) and
  `api/v1/admin/margin-floors`, each with the permission of §1.
- Buyer: `GET api/v1/companies/{companyId}/prices?variantIds=…` (at most 100 ids), members of that company only,
  any other company → 404. Response header `Cache-Control: private, no-store`.
- **Public caches never hold company prices.** The public storefront and search endpoints (`Cache-Control: public`),
  the Redis keys `sf:*` and the search index carry only public prices (list, sale, `General` breaks) and answer the
  same bytes for a guest and for a signed-in company member. The web app renders product pages from public data and
  fetches the company price separately, per user, never through ISR, the Next.js data cache or a CDN.
- Public prices in those caches can lag by their TTL after a `General` list starts or ends. The order always uses
  the live resolver, and §9 catches a difference.

### 12. Owner decisions
**Decided 2026-11-13 (owner delegated to the main session; reversible as noted):**
1. **Rule A, lowest valid price wins.** A signed-in company never pays more than a guest; nobody re-checks contracts when
   public prices drop. Switching to B later is one function plus its property test.
2. **(a): the catalogue price is the VAT-inclusive public price for everyone.** A company sees price ÷ (1 + VAT) plus the
   VAT line and pays the same total as a guest (to within rounding of the unit price to whole poisha). D2.5 changes the two
   `OrderServiceTests` expectations (company total 105,000 → 100,000 for a 1,000.00 item).
3. **Margin floor default: 10 % minimum markup on cost, global; no per-brand/category rows at launch; below-cost refused
   (`Pricing:AllowBelowCost = false`).** D2.4 seeds the single global row (reference data, so fail-closed "no global floor"
   never bites in a fresh database); the owner changes values or adds brand/category rows through D2.9. Permission
   defaults: `pricelists.edit` = catalogue/sales staff; `pricelists.approve` = finance or admin, never the author;
   `pricelists.approve_below_floor` and `pricing.manage_floors` = admin only. Role names are settled in G5.
4. **No group lists at launch (D2.14 stays after launch).** Contracts (`Company` lists) are the must-have; public quantity
   breaks stay in the model and are used only when real break prices exist.
5. **Defaults confirmed:** longest validity 366 days; a new list starts 60 seconds after approval at the earliest.
6. **Third-party sellers:** contract lists cover MECHWARE's own store products only until the marketplace launches (H5);
   the discount cost question is revisited then.

Original options (kept for the record):
1. **Resolution rule** (§5.1): A lowest valid wins (**recommended**, the brief) or B precedence.
2. **What the catalogue price means for a company** (§2 finding). (a) **recommended:** the catalogue price is the
   VAT-inclusive public price; a company sees price ÷ (1 + VAT) plus the VAT line and pays the same total as a
   guest (to within rounding of the unit price to whole poisha). Changes two expectations in `OrderServiceTests`.
   (b) the catalogue price is VAT-exclusive; guests are shown price × (1 + VAT): every public price rises by the
   VAT rate. (c) keep today's behaviour: a company pays the VAT rate more than a guest for the same item.
3. **Margin floors:** the values (global default; per brand, per category), confirmation that "floor" means markup
   on cost, whether below-cost entries may ever be approved (`AllowBelowCost`), and who gets which permission (§1).
4. **Scope at launch:** contract (`Company`) lists are the must-have. Group lists: **recommended after launch**
   (five pilot buyers are served by contracts; assigning a company to a group needs its own second approver).
   Public quantity breaks (`General`): built with the model; use them only if real break prices exist.
5. Smaller defaults to confirm: longest validity 366 days; activation delay 60 seconds.
6. If third-party sellers go live: may MECHWARE put their products on a contract list, and who carries the
   discount (the seller's earning is calculated on the discounted net)?

### 13. Not in D2
Percentage or rule-based lists ("15 % off all Siemens"), prices per delivery site, other currencies, prices tied to
the USD rate, CSV import of entries, e-mails about new lists, tamper-evident audit (threat model v1, H3).

## Consequences
### Positive
- One place decides a price, and it is pure and property-tested; orders, the buyer price endpoint and quotes share it.
- An approved price can always be explained: who wrote it, who approved it, which cost and floor applied, and
  which version an order used.
- No background job changes prices; time decides, from committed data.
- The existing engines (`PricingEngine`, `OrderPricing`, `PromotionEngine`) are not rewritten.

### Negative
- Two tax bases live in one table (public for `General`, net for negotiated). The scope decides, never the user.
- Company prices cost one extra authenticated request per page and cannot be cached publicly; price sort and
  filter in search use public prices.
- A price change takes at least `ActivationDelaySeconds` and two people.
- Two colluding staff members, or one person with two staff accounts, can still approve a bad list. The controls
  are mandatory two-factor sign-in for staff, the audit trail and the below-floor permission.
- `order_items` grows by five columns and `PlaceOrderRequest` by one optional field.
