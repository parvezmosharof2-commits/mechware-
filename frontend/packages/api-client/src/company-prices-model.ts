/**
 * D2.13: company (contract) prices shown to a signed-in company buyer. Pure types and helpers (no runtime imports) so they
 * run under `node --test`. Hand-typed from the D2.10 HANDOFF: the OpenAPI document has no response schema for the route.
 *
 * Money rule: company prices are NET of VAT; the storefront's public price is VAT-inclusive. The two are never put side by
 * side without a label, and "cheaper" is decided on one basis only (net vs net, via `publicUnitPriceMinorNet`).
 */

export type CompanyPriceSource = 'list' | 'sale' | 'quantity_break' | 'contract';

export interface CompanyPriceBreak {
  minQty: number;
  unitPriceMinorNet: number;
}

export interface CompanyPriceItem {
  variantId: string;
  status: 'available' | 'unavailable';
  quantity?: number;
  unitPriceMinorNet?: number;
  publicUnitPriceMinorNet?: number;
  guestUnitPriceMinorInclVat?: number;
  source?: CompanyPriceSource | (string & {});
  priceListId?: string;
  contractRef?: string;
  validToUtc?: string;
  taxMode?: string;
  /** Percent (15) or fraction (0.15): `vatPercent` tells them apart. */
  vatRate?: number;
  lineNetMinor?: number;
  lineTaxMinor?: number;
  lineTotalMinor?: number;
  breaks?: CompanyPriceBreak[];
  quantityProblem?: string;
}

export interface CompanyPricesResponse {
  data: { companyId: string; asOfUtc: string; items: CompanyPriceItem[] };
}

export const MAX_PRICE_ITEMS = 100;

export interface PriceAsk {
  variantId: string;
  quantity: number;
}

/**
 * The `items=` query value: `variantId:qty` joined by commas. Each variant once (first ask wins), quantity a whole number
 * 1..10,000, at most 100 items (extra asks are dropped, never sent). Returns '' when nothing valid is left.
 */
export function buildItemsQuery(asks: readonly PriceAsk[]): string {
  const seen = new Set<string>();
  const parts: string[] = [];
  for (const ask of asks) {
    if (parts.length >= MAX_PRICE_ITEMS) break;
    const id = ask.variantId.trim().toLowerCase();
    if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(id) || seen.has(id)) continue;
    const qty = Number.isFinite(ask.quantity) ? Math.min(10000, Math.max(1, Math.floor(ask.quantity))) : 1;
    seen.add(id);
    parts.push(`${id}:${qty}`);
  }
  return parts.join(',');
}

/** A stable key for a set of asks (for in-memory caching and "did the question change"). */
export function asksKey(companyId: string, asks: readonly PriceAsk[]): string {
  return `${companyId}|${buildItemsQuery(asks)}`;
}

/** VAT as a percent for display: the API may send 15 or 0.15. */
export function vatPercent(vatRate: number | undefined): number {
  if (vatRate === undefined || !Number.isFinite(vatRate) || vatRate < 0) return 0;
  const pct = vatRate <= 1 ? vatRate * 100 : vatRate;
  return Math.round(pct * 100) / 100;
}

export type PriceDisplayKind = 'none' | 'contract' | 'yourPrice';

export interface PriceDisplay {
  kind: PriceDisplayKind;
  unitNetMinor: number;
  lineNetMinor: number;
  lineTaxMinor: number;
  lineTotalMinor: number;
  quantity: number;
  vatPercent: number;
  contractRef?: string;
  /** Exclusive end of the contract (ISO); show the previous day in Dhaka time. */
  validToUtc?: string;
  /** The public price on the same net basis, and the guest (VAT-inclusive) price, each only for a labelled comparison. */
  publicUnitNetMinor?: number;
  guestUnitInclVatMinor?: number;
  /** Whole minor units saved per unit against the public price on the net basis; 0 when not cheaper. */
  savingPerUnitNetMinor: number;
}

/**
 * What to show for one answered item.
 * - 'none': unavailable, no usable numbers, a plain `list` source, or a company price that is not cheaper than the public one
 *   (the storefront shows the public price; nothing extra).
 * - 'contract': source is contract and cheaper than public: "Your contract price (ref ...)".
 * - 'yourPrice': quantity_break or sale and cheaper: "Your price".
 * A contract price at or above the public one never wins (the API already resolves "lowest valid price"), so equal means none.
 */
export function pickDisplay(item: CompanyPriceItem | undefined): PriceDisplay | null {
  if (!item || item.status !== 'available') return null;
  const { unitPriceMinorNet: unit, publicUnitPriceMinorNet: pub, quantity, lineNetMinor, lineTaxMinor, lineTotalMinor } = item;
  if (unit === undefined || quantity === undefined || lineNetMinor === undefined || lineTaxMinor === undefined || lineTotalMinor === undefined) return null;
  const special = item.source === 'contract' || item.source === 'quantity_break' || item.source === 'sale';
  const cheaper = pub === undefined ? special : unit < pub;
  const kind: PriceDisplayKind = !special || !cheaper ? 'none' : item.source === 'contract' ? 'contract' : 'yourPrice';
  return {
    kind,
    unitNetMinor: unit,
    lineNetMinor,
    lineTaxMinor,
    lineTotalMinor,
    quantity,
    vatPercent: vatPercent(item.vatRate),
    contractRef: item.source === 'contract' ? item.contractRef : undefined,
    validToUtc: item.source === 'contract' ? item.validToUtc : undefined,
    publicUnitNetMinor: pub,
    guestUnitInclVatMinor: item.guestUnitPriceMinorInclVat,
    savingPerUnitNetMinor: pub !== undefined && unit < pub ? pub - unit : 0
  };
}

export interface TierRow {
  minQty: number;
  unitNetMinor: number;
  /** True when this tier is the one the asked quantity falls into. */
  current: boolean;
}

/** Break table sorted by quantity; empty when the server sent none. `current` marks the tier of `quantity`. */
export function tierRows(item: CompanyPriceItem | undefined, quantity: number): TierRow[] {
  const breaks = [...(item?.breaks ?? [])].filter((b) => b.minQty >= 1).sort((a, b) => a.minQty - b.minQty);
  let currentIndex = -1;
  breaks.forEach((b, i) => {
    if (quantity >= b.minQty) currentIndex = i;
  });
  return breaks.map((b, i) => ({ minQty: b.minQty, unitNetMinor: b.unitPriceMinorNet, current: i === currentIndex }));
}

/** The last day the contract price applies: `validToUtc` is exclusive, so show the day before, in Dhaka (UTC+6, no DST). */
export function contractLastDay(validToUtc: string | undefined): string | null {
  if (!validToUtc) return null;
  const end = Date.parse(validToUtc);
  if (!Number.isFinite(end)) return null;
  const dhaka = new Date(end + 6 * 3600_000 - 1);
  return dhaka.toISOString().slice(0, 10);
}

/** Index answers by variant id for quick lookup. */
export function indexItems(response: CompanyPricesResponse | undefined | null): Map<string, CompanyPriceItem> {
  const map = new Map<string, CompanyPriceItem>();
  for (const item of response?.data?.items ?? []) map.set(item.variantId.toLowerCase(), item);
  return map;
}

export interface BuyerCompanyLike {
  company: { id: string; legalName: string; status: string };
  roles: number;
}

const BUYER_ROLE = 2;

/** Active companies where the user holds the Buyer role (the same people the API lets see prices and place orders). */
export function buyerCompanies<T extends BuyerCompanyLike>(mine: readonly T[]): T[] {
  return mine.filter((m) => m.company.status === 'active' && (m.roles & BUYER_ROLE) !== 0);
}

/** Which company the prices are for: the remembered one if still usable, else the first. */
export function chooseCompany<T extends BuyerCompanyLike>(usable: readonly T[], preferredId?: string | null): T | undefined {
  return usable.find((m) => m.company.id === preferredId) ?? usable[0];
}
