/**
 * Staff lookups for the console quote editor (D4.18 routes, D4.19 screens). Pure shapes and helpers, no runtime imports (Node's type
 * stripping must be able to test it). All three lookups need quotes.edit; none carries cost, phone or any other private field.
 */
import type { EditorFee, EditorLine } from './quote-editor-model';

export interface QuoteSite {
  id: string; name: string; line1: string; line2: string | null; upazila: string | null; district: string | null; division: string | null;
  postalCode: string | null; isDefaultDelivery: boolean;
}
export interface QuoteContact { userId: string; fullName: string; email: string; roles: string[] }
export interface QuoteSeller { variantId: string; sellerId: string; sellerName: string }

/** `variantIds=a&variantIds=b`, distinct, at most 50 (the route's limit); empty when there is nothing to ask. */
export function sellersQuery(variantIds: readonly string[]): string {
  const seen = [...new Set(variantIds.map((v) => v.trim().toLowerCase()).filter(Boolean))].slice(0, 50);
  return seen.map((v) => `variantIds=${encodeURIComponent(v)}`).join('&');
}

/** "Gazipur Plant: Plot 5, Tongi, Gazipur (default)": the one line an option shows. */
export function siteOptionText(s: QuoteSite): string {
  const place = [s.line1, s.upazila, s.district].map((x) => (x ?? '').trim()).filter(Boolean).join(', ');
  return `${s.name}${place ? `: ${place}` : ''}${s.isDefaultDelivery ? ' (default)' : ''}`;
}

/** The site a fixed delivery should start with: the saved one if it is still listed, else the default, else none (the person chooses). */
export function initialSiteId(sites: readonly QuoteSite[], saved: string): string {
  const keep = sites.find((s) => s.id.toLowerCase() === saved.trim().toLowerCase());
  if (keep) return keep.id;
  return (sites.find((s) => s.isDefaultDelivery) ?? (sites.length === 1 ? sites[0] : null))?.id ?? '';
}

const ROLE_WORDS: Record<string, string> = { admin: 'Admin', buyer: 'Buyer', approver: 'Approver', requester: 'Requester' };
export const roleWords = (roles: readonly string[]): string => roles.map((r) => ROLE_WORDS[r] ?? r).join(', ');

/** "Rahim Uddin, rahim@firm.bd (Admin, Buyer)". */
export const contactOptionText = (c: QuoteContact): string => `${c.fullName}, ${c.email}${c.roles.length ? ` (${roleWords(c.roles)})` : ''}`;

/** The sellers of these lines, one entry per seller in the order they first appear. Variants the lookup did not know are left out. */
export function expectedSellers(lines: readonly Pick<EditorLine, 'variantId'>[], sellers: readonly QuoteSeller[]): { sellerId: string; sellerName: string }[] {
  const byVariant = new Map(sellers.map((s) => [s.variantId.toLowerCase(), s]));
  const out = new Map<string, { sellerId: string; sellerName: string }>();
  for (const l of lines) {
    const s = byVariant.get(l.variantId.toLowerCase());
    if (s && !out.has(s.sellerId.toLowerCase())) out.set(s.sellerId.toLowerCase(), { sellerId: s.sellerId, sellerName: s.sellerName });
  }
  return [...out.values()];
}

/** Lines whose seller the lookup did not return (so no fee row can be made for them). */
export const linesWithoutSeller = (lines: readonly Pick<EditorLine, 'variantId' | 'sku'>[], sellers: readonly QuoteSeller[]): string[] =>
  lines.filter((l) => !sellers.some((s) => s.variantId.toLowerCase() === l.variantId.toLowerCase())).map((l) => l.sku);

/**
 * One fee row for each seller of the lines: typed fees are kept, a seller that gained a line gets an empty row, a seller that lost
 * all its lines loses its row. Returns the SAME array when nothing changes, so the caller can skip a state update.
 */
export function syncFees(fees: readonly EditorFee[], expected: readonly { sellerId: string; sellerName: string }[], makeKey: () => string): readonly EditorFee[] {
  const want = new Map(expected.map((e) => [e.sellerId.toLowerCase(), e]));
  const kept: EditorFee[] = [];
  let changed = false;
  for (const f of fees) {
    const e = want.get(f.sellerId.trim().toLowerCase());
    if (!e) { changed = true; continue; }
    if (f.label !== e.sellerName) { changed = true; kept.push({ ...f, label: e.sellerName }); } else kept.push(f);
  }
  for (const e of expected) {
    if (!kept.some((f) => f.sellerId.trim().toLowerCase() === e.sellerId.toLowerCase())) { changed = true; kept.push({ key: makeKey(), sellerId: e.sellerId, label: e.sellerName, feeText: '' }); }
  }
  return changed ? kept : fees;
}

/** "Minimum order 5. Sold in packs of 5." or '' when neither limits anything. */
export function quantityHint(minOrderQty: number | null | undefined, packSize: number | null | undefined): string {
  const parts: string[] = [];
  if (minOrderQty && minOrderQty > 1) parts.push(`Minimum order ${minOrderQty}.`);
  if (packSize && packSize > 1) parts.push(`Sold in packs of ${packSize}.`);
  return parts.join(' ');
}

/** What a failed lookup says, in words, with the lookup's name. */
export function lookupFailureText(what: string, status: number | null): string {
  if (status === 403) return `Your role may not load ${what} (it needs quotes.edit). Ask an Admin.`;
  if (status === 404) return `The company was not found, so ${what} could not be loaded.`;
  if (status === null) return `Could not load ${what}: the server cannot be reached. Check your connection and try again.`;
  return `Could not load ${what}. Try again in a moment.`;
}
