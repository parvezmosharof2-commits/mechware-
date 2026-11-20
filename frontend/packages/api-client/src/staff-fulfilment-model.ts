/**
 * Manual fulfilment (F1.3): the next step of an order, the checks of the ship form and the words for a refusal. Shapes and rules follow
 * docs/modules/F1.md HANDOFF F1.1. No runtime imports (Node's type stripping must run it). Everything here only helps the person: the API checks
 * permission, fields, rowVersion and state again and its answer wins.
 */

export const FULFIL_PERMISSION = 'orders.fulfil';
export const canFulfilOrders = (permissions: readonly string[]): boolean => permissions.some((p) => p.toLowerCase() === FULFIL_PERMISSION);

export interface StaffShipment { id: string; courierName: string; trackingNumber: string; trackingUrl: string | null; shippedAtUtc: string; deliveredAtUtc: string | null; createdBy: string | null }
/** What the buyer sees: no shipment id, no staff id. */
export interface BuyerShipment { courierName: string; trackingNumber: string; trackingUrl: string | null; shippedAtUtc: string; deliveredAtUtc: string | null }

export type FulfilStep = 'processing' | 'packed' | 'ship' | 'delivered';
export interface FulfilAction { step: FulfilStep; label: string; /** Finishes the sentence "Order MW-... ". */ doneText: string }

const ACTIONS: Record<FulfilStep, FulfilAction> = {
  processing: { step: 'processing', label: 'Start processing', doneText: 'is now being processed.' },
  packed: { step: 'packed', label: 'Mark packed', doneText: 'is now marked packed.' },
  ship: { step: 'ship', label: 'Ship…', doneText: 'is now shipped.' },
  delivered: { step: 'delivered', label: 'Mark delivered', doneText: 'is now marked delivered.' }
};
const NEXT: Record<string, FulfilStep> = { confirmed: 'processing', processing: 'packed', packed: 'ship', shipped: 'delivered', out_for_delivery: 'delivered' };

/** The one step the order can take now, or null. */
export const nextFulfilStep = (status: string): FulfilAction | null => (NEXT[status] ? ACTIONS[NEXT[status]!] : null);

/** Why there is no button, in words (shown to someone who may fulfil). */
export function noStepReason(status: string): string {
  switch (status) {
    case 'pending_approval': return 'This order is waiting for a company approver, so it cannot be processed yet.';
    case 'pending_payment': return 'This order is not paid or accepted on credit yet, so it cannot be processed yet.';
    case 'delivered': case 'completed': return 'This order is delivered. There is nothing left to do.';
    case 'cancelled': return 'This order is cancelled, so it cannot be processed or shipped.';
    case 'delivery_failed': case 'returned': case 'return_requested': case 'return_approved': case 'refunded':
      return 'This order is in a return or failed-delivery state. Those steps are not done from here yet.';
    default: return 'No fulfilment step is available for this order now.';
  }
}
export const FULFIL_MISSING_PERMISSION_TEXT = 'You cannot move this order along: your role does not hold the orders.fulfil permission (support and admin do).';

// ---------- ship form ----------

export const TRACKING_URL_HOSTS = ['steadfast.com.bd', 'pathao.com', 'redx.com.bd', 'paperfly.com.bd', 'ecourier.com.bd'] as const;
export const TRACKING_URL_HELP = `Optional. It must be the courier's own tracking page: https, on ${TRACKING_URL_HOSTS.join(', ')} (or a page under them). Buyers see it as a link, so a link to any other site is refused. The tracking number alone is enough.`;

export interface ShipFields { courierName: string; trackingNumber: string; trackingUrl: string }
export type ShipProblems = Partial<Record<keyof ShipFields, string>>;

export const cleanCourierName = (s: string): string => s.replace(/\s+/g, ' ').trim();

/** The tracking link as a safe absolute URL (https, allowed courier host, no user name, default port, at most 500), else null. */
export function safeTrackingUrl(raw: string | null | undefined): string | null {
  const text = (raw ?? '').trim();
  if (!text || text.length > 500) return null;
  let u: URL;
  try { u = new URL(text); } catch { return null; }
  if (u.protocol !== 'https:' || u.username || u.password || u.port) return null;
  const host = u.hostname.toLowerCase();
  if (!TRACKING_URL_HOSTS.some((h) => host === h || host.endsWith(`.${h}`))) return null;
  return u.href;
}

/** Checks every field of the ship form before anything is sent; an empty object means all fine. */
export function shipProblems(f: ShipFields): ShipProblems {
  const out: ShipProblems = {};
  const courier = cleanCourierName(f.courierName);
  if (courier.length < 2 || courier.length > 80) out.courierName = 'The courier name must be 2 to 80 characters.';
  else if (/[\u0000-\u001f<>]/.test(courier)) out.courierName = 'The courier name can only hold plain text.';
  const number = f.trackingNumber.trim();
  if (!number) out.trackingNumber = 'Enter the tracking number.';
  else if (number.length > 60) out.trackingNumber = 'The tracking number can be at most 60 characters.';
  else if (!/^[A-Za-z0-9-]+$/.test(number)) out.trackingNumber = 'The tracking number can only hold letters, digits and dashes (no spaces).';
  const url = f.trackingUrl.trim();
  if (url) {
    if (url.length > 500) out.trackingUrl = 'The tracking link can be at most 500 characters.';
    else if (!/^https:\/\//i.test(url)) out.trackingUrl = 'The tracking link must start with https://';
    else if (!safeTrackingUrl(url)) out.trackingUrl = "The tracking link must be the courier's own page on a known courier site (no user name or port in it). Leave it empty if you are not sure.";
  }
  return out;
}

/** The request body of a ship call; an empty link is sent as null. */
export const shipBody = (rowVersion: number, f: ShipFields) => ({
  rowVersion, courierName: cleanCourierName(f.courierName), trackingNumber: f.trackingNumber.trim(), trackingUrl: f.trackingUrl.trim() || null
});

// ---------- errors ----------

export const FULFIL_RELOAD_CODES: ReadonlySet<string> = new Set(['ORDER_CHANGED', 'ORDER_INVALID_TRANSITION']);
export const shouldReloadFulfil = (code: string | null | undefined): boolean => !!code && FULFIL_RELOAD_CODES.has(code);

const NEEDS: Record<FulfilStep, string> = {
  processing: 'Only a confirmed order can start processing',
  packed: 'Only an order that is being processed can be packed',
  ship: 'Only a packed order can be shipped',
  delivered: 'Only a shipped order can be marked delivered'
};

export interface FulfilErrorLike { status: number | null; code: string | null; message: string; fields: Record<string, string> }
export function fulfilErrorText(step: FulfilStep, e: FulfilErrorLike): string {
  if (e.code === 'ORDER_CHANGED') return 'Someone else changed this order while you were looking at it, so nothing was done. The page was reloaded: check what it shows now and try again if the step is still needed.';
  if (e.code === 'ORDER_INVALID_TRANSITION') return `${NEEDS[step]} (it may have been cancelled or moved on by someone else). The page was reloaded and shows its status now.`;
  const first = Object.values(e.fields)[0];
  if (e.status === 400 && first) return first;
  if (e.status === null) return 'The server cannot be reached. Check your connection and try again.';
  if (e.status === 401) return 'Your session ended. Sign in again.';
  if (e.status === 403) return 'You do not have permission to fulfil orders.';
  if (e.status === 404) return 'This order was not found.';
  if (e.status === 429) return 'Too many requests. Wait a moment and try again.';
  if (e.status >= 500) return 'The server had a problem. Try again in a moment; if it keeps happening, tell the technical team.';
  return e.message || 'The request was refused.';
}

/** Server field errors (by field name, any case) mapped onto the three ship fields. */
export function shipFieldErrors(fields: Record<string, string>): ShipProblems {
  const out: ShipProblems = {};
  for (const [k, v] of Object.entries(fields)) {
    const key = k.toLowerCase();
    if (key === 'couriername') out.courierName = v;
    else if (key === 'trackingnumber') out.trackingNumber = v;
    else if (key === 'trackingurl') out.trackingUrl = v;
  }
  return out;
}

// ---------- buyer ----------

/** True while nothing has shipped yet: the buyer page shows the status in words instead of a shipment. */
export const beforeShipping = (status: string): boolean => ['pending_approval', 'pending_payment', 'confirmed', 'processing', 'packed'].includes(status);
