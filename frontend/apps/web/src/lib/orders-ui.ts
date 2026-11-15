import type { OrderStatusTone } from '@mechware/api-client';

/** A server timestamp as a Date. A value without a zone designator is UTC (the API stores UTC). Null for junk. */
export function parseServerDate(iso: string | null | undefined): Date | null {
  if (!iso) return null;
  const withZone = /(Z|[+-]\d{2}:?\d{2})$/.test(iso) ? iso : `${iso}Z`;
  const d = new Date(withZone);
  return Number.isNaN(d.getTime()) ? null : d;
}

/** "16 Nov 2026" / "১৬ নভেম্বর ২০২৬" in Bangladesh time, so the day does not depend on the visitor's own zone. Empty for junk. */
export function formatOrderDate(iso: string | null | undefined, locale: string): string {
  const d = parseServerDate(iso);
  if (!d) return '';
  return new Intl.DateTimeFormat(locale === 'bn' ? 'bn-BD' : 'en-GB', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'Asia/Dhaka' }).format(d);
}

export type OrderLoadError = 'notFound' | 'unavailable' | 'other';

/** 404 is "not found / not yours" (the API never says which); network and 5xx errors are "unavailable" and can be retried. */
export function classifyOrderError(error: unknown): OrderLoadError {
  const status = (error as { status?: unknown } | null)?.status;
  if (status === 404) return 'notFound';
  if (typeof status !== 'number' || status >= 500 || status === 429) return 'unavailable';
  return 'other';
}

export function orderHref(prefix: string, orderNumber: string): string {
  return `${prefix}/account/orders/${encodeURIComponent(orderNumber)}`;
}

export function ordersListHref(prefix: string, page: number): string {
  return page > 1 ? `${prefix}/account/orders?page=${page}` : `${prefix}/account/orders`;
}

/** Tailwind classes for the status chip (colour plus the text label, never colour alone). */
export function statusChipClass(tone: OrderStatusTone): string {
  switch (tone) {
    case 'pending':
      return 'border-status-pending/50 bg-status-pending/10 text-text';
    case 'progress':
      return 'border-accent/50 bg-accent/10 text-text';
    case 'done':
      return 'border-status-online/50 bg-status-online/10 text-text';
    case 'problem':
      return 'border-fault/50 bg-fault/10 text-text';
    default:
      return 'border-surface-border bg-surface text-text';
  }
}
