/**
 * In-app notifications (F3d): wire types and pure helpers for the bell and the list page. Hand-written from the F3c handoff.
 * No runtime imports, so Node's type stripping can test it. The API owns who sees what; this only shapes the display.
 */

export const NOTIFICATION_KEYS = [
  'order_shipped', 'order_delivered', 'invoice_issued', 'return_approved', 'return_rejected', 'return_received', 'return_money_back',
  'quote_sent', 'quote_expiring', 'quote_withdrawn'
] as const;
export const NOTIFICATION_PAGE_SIZE = 50;
export const BELL_ITEMS = 10;
export const NOTIFICATION_POLL_MS = 60_000;

export interface BuyerNotification {
  id: string; kind: string; titleKey: string; params: Record<string, unknown> | null; link: string | null; createdAt: string; readAt: string | null;
}
export interface NotificationPage { items: BuyerNotification[]; page: number; pageSize: number; total: number }

/** i18n key (under `Notifications.title`) for a server title key (`notifications.order_shipped`); anything unknown gets `generic`. */
export function notificationMessageKey(titleKey: string | null | undefined): string {
  const name = (titleKey ?? '').replace(/^notifications\./, '');
  return (NOTIFICATION_KEYS as readonly string[]).includes(name) ? name : 'generic';
}

/** Params as plain strings for the message (numbers are kept, objects and nulls dropped). Never HTML. */
export function notificationParams(params: Record<string, unknown> | null | undefined): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(params ?? {})) {
    if (typeof v === 'string' || typeof v === 'number') out[k] = String(v);
  }
  return out;
}

/** A link is used only when it is one site-relative path: starts with a single "/", no "//", no backslash, no control characters. */
export function safeNotificationLink(link: string | null | undefined): string | null {
  if (!link || link[0] !== '/' || link.startsWith('//') || link.includes('\\')) return null;
  for (let i = 0; i < link.length; i++) if (link.charCodeAt(i) < 0x20 || link.charCodeAt(i) === 0x7f) return null;
  return link;
}

/** The link as the storefront serves it: the default (English) locale has no prefix, so `/en/x` becomes `/x`. */
export function localeLink(link: string | null | undefined): string | null {
  const safe = safeNotificationLink(link);
  if (!safe) return null;
  return safe === '/en' ? '/' : safe.startsWith('/en/') ? safe.slice(3) : safe;
}

/** "28 Nov 2026, 2:30 am" in Bangladesh time (UTC+6), whatever zone the visitor is in. Empty for junk. */
export function formatNotificationTime(iso: string | null | undefined, locale: string): string {
  if (!iso) return '';
  const d = new Date(/(Z|[+-]\d{2}:?\d{2})$/.test(iso) ? iso : `${iso}Z`);
  if (Number.isNaN(d.getTime())) return '';
  return new Intl.DateTimeFormat(locale === 'bn' ? 'bn-BD' : 'en-GB', { day: 'numeric', month: 'short', year: 'numeric', hour: 'numeric', minute: '2-digit', timeZone: 'Asia/Dhaka' }).format(d);
}

/** Badge text: nothing at 0, "99+" from 100. */
export function badgeText(count: number): string {
  if (!Number.isFinite(count) || count <= 0) return '';
  return count > 99 ? '99+' : String(Math.floor(count));
}

export function notificationListQuery(unreadOnly: boolean, page: number): string {
  const p = Number.isFinite(page) && page >= 1 ? Math.floor(page) : 1;
  return `${unreadOnly ? 'unread=true&' : ''}page=${p}`;
}
