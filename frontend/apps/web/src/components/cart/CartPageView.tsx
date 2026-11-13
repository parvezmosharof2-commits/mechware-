'use client';

import * as React from 'react';
import Link from 'next/link';
import { useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import { EmptyState } from '@mechware/ui/src/components/EmptyState';
import {
  blockedVariantIds,
  canProceedToCheckout,
  classifyCartError,
  clampCartQuantity,
  formatPriceMinor,
  groupNotices,
  minCartQuantity,
  noticeMessage,
  noticeTone,
  shouldRefetchAfter,
  stepCartQuantity,
  type CartErrorKind,
  type CartLine,
  type CartNotice,
  type CartView
} from '@mechware/api-client';
import { useAuth } from '../../lib/auth';
import { announceCart } from '../../lib/cart-events';
import { actionErrorKey } from '../../lib/cart-ui';
import { CartSkeleton } from './CartSkeleton';

type Translate = ReturnType<typeof useTranslations>;

/** Wait after the last keystroke or step press before the quantity is sent. */
const DEBOUNCE_MS = 600;

function Notice({ notice, currency, locale, t }: { notice: CartNotice; currency: string; locale: string; t: Translate }) {
  const { key, values } = noticeMessage(notice, (minor) => formatPriceMinor(minor, currency, locale));
  const blocking = noticeTone(notice.kind) === 'blocking';
  return (
    <p
      className={`flex gap-2 rounded-md border px-3 py-2 text-sm text-text ${blocking ? 'border-fault/50 bg-fault/10' : 'border-status-pending/50 bg-status-pending/10'}`}
    >
      <span className="sr-only">{t('noticeLabel')}: </span>
      <span aria-hidden="true" className={`font-bold ${blocking ? 'text-fault' : 'text-status-pending'}`}>!</span>
      <span>{t(key, values)}</span>
    </p>
  );
}

interface LineProps {
  line: CartLine;
  draft: string | undefined;
  notices: CartNotice[];
  currency: string;
  locale: string;
  disabled: boolean;
  t: Translate;
  onDraft: (line: CartLine, text: string) => void;
  onStep: (line: CartLine, direction: 1 | -1) => void;
  onCommit: (line: CartLine) => void;
  onRemove: (line: CartLine) => void;
}

function CartLineRow({ line, draft, notices, currency, locale, disabled, t, onDraft, onStep, onCommit, onRemove }: LineProps) {
  const min = minCartQuantity(line);
  const shown = draft ?? String(line.quantity);
  const current = Number(shown);
  const pack = Math.max(1, line.packSize);
  const label = 'text-xs text-text-muted md:sr-only';
  const stepBtn =
    'flex h-11 w-11 items-center justify-center rounded-md border border-surface-border-strong bg-bg-muted text-lg text-text hover:bg-surface focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:cursor-not-allowed disabled:opacity-50';
  return (
    <li className="grid gap-x-4 gap-y-3 border-b border-surface-border px-4 py-4 last:border-b-0 md:grid-cols-[minmax(0,1fr)_12rem_8rem_8rem_auto] md:items-start">
      <div className="min-w-0">
        <p className="break-words font-semibold text-text">{line.title}</p>
        <p className="mt-1 text-xs text-text-muted">
          {t('partNumber')}: <span className="break-all font-mono text-text">{line.sku}</span>
        </p>
      </div>

      <div>
        <div className="flex items-center gap-1">
          <button
            type="button"
            className={stepBtn}
            disabled={disabled || !Number.isFinite(current) || stepCartQuantity(current, -1, line) === null}
            aria-label={t('decrease', { sku: line.sku })}
            onClick={() => onStep(line, -1)}
          >
            <span aria-hidden="true">−</span>
          </button>
          <input
            type="number"
            inputMode="numeric"
            min={min}
            step={pack}
            value={shown}
            disabled={disabled}
            aria-label={t('quantityOf', { sku: line.sku })}
            onChange={(e) => onDraft(line, e.target.value)}
            onBlur={() => onCommit(line)}
            onKeyDown={(e) => e.key === 'Enter' && onCommit(line)}
            className="h-11 w-20 rounded-md border border-surface-border-strong bg-bg-muted px-2 text-center text-sm text-text outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:opacity-60"
          />
          <button
            type="button"
            className={stepBtn}
            disabled={disabled || !Number.isFinite(current)}
            aria-label={t('increase', { sku: line.sku })}
            onClick={() => onStep(line, 1)}
          >
            <span aria-hidden="true">+</span>
          </button>
        </div>
        {(pack > 1 || min > 1) && <p className="mt-1 text-xs text-text-muted">{pack > 1 ? t('qtyHintPack', { min, pack }) : t('qtyHintMin', { min })}</p>}
      </div>

      <div>
        <p className={label}>{t('colUnitPrice')}</p>
        <p className="text-sm text-text">{formatPriceMinor(line.unitPriceMinor, currency, locale)}</p>
      </div>

      <div>
        <p className={label}>{t('colTotal')}</p>
        <p className="font-semibold text-text">{formatPriceMinor(line.lineTotalMinor, currency, locale)}</p>
      </div>

      <div>
        <Button type="button" variant="ghost" size="sm" disabled={disabled} aria-label={t('removeLabel', { sku: line.sku })} onClick={() => onRemove(line)}>
          {t('remove')}
        </Button>
      </div>

      {notices.length > 0 && (
        <div className="space-y-2 md:col-span-5">
          {notices.map((n, i) => (
            <Notice key={`${n.kind}-${i}`} notice={n} currency={currency} locale={locale} t={t} />
          ))}
        </div>
      )}
    </li>
  );
}

/**
 * The cart page body. It runs in the browser on purpose: the guest cart cookie is HttpOnly and belongs to the API origin,
 * so only the browser can send it. Everything shown (prices, totals, notices) is exactly what the server returned;
 * nothing is computed or guessed here, and the controls are locked while a request is in flight.
 */
export function CartPageView({ locale }: { locale: 'en' | 'bn' }) {
  const t = useTranslations('Cart');
  const { status } = useAuth();
  const prefix = locale === 'bn' ? '/bn' : '';
  const [cart, setCart] = React.useState<CartView | null>(null);
  const [loadError, setLoadError] = React.useState<CartErrorKind | null>(null);
  const [actionError, setActionError] = React.useState<CartErrorKind | null>(null);
  const [pending, setPending] = React.useState(0);
  const [statusText, setStatusText] = React.useState('');
  const [drafts, setDrafts] = React.useState<Record<string, string>>({});
  const timers = React.useRef(new Map<string, number>());
  const queue = React.useRef<Promise<unknown>>(Promise.resolve());
  const mounted = React.useRef(true);
  const headingRef = React.useRef<HTMLDivElement>(null);

  React.useEffect(() => {
    mounted.current = true;
    const pendingTimers = timers.current;
    return () => {
      mounted.current = false;
      pendingTimers.forEach((id) => window.clearTimeout(id));
    };
  }, []);

  const apply = React.useCallback((next: CartView) => {
    if (!mounted.current) return;
    setCart(next);
    setLoadError(null);
    announceCart(next.lineCount);
  }, []);

  const fetchCart = React.useCallback(async (): Promise<void> => {
    const { cartApi } = await import('../../lib/cart-client');
    try {
      apply(await cartApi.get());
    } catch (error) {
      if (!mounted.current) return;
      const kind = classifyCartError(error);
      // Never turn a failure into an empty cart: keep what is on screen (if anything) and say what went wrong.
      setCart((current) => {
        if (current) setActionError(kind);
        else setLoadError(kind);
        return current;
      });
    }
  }, [apply]);

  /** Runs one change at a time, in order, with controls locked while it is in flight. */
  const run = React.useCallback(
    (change: (api: typeof import('../../lib/cart-client').cartApi) => Promise<CartView>, doneText: string) => {
      setPending((n) => n + 1);
      setActionError(null);
      const job = queue.current.then(async () => {
        const { cartApi } = await import('../../lib/cart-client');
        try {
          apply(await change(cartApi));
          if (mounted.current) setStatusText(doneText);
        } catch (error) {
          const kind = classifyCartError(error);
          if (mounted.current) setActionError(kind);
          if (shouldRefetchAfter(kind)) await fetchCart();
        } finally {
          if (mounted.current) setPending((n) => n - 1);
        }
      });
      queue.current = job;
      return job;
    },
    [apply, fetchCart]
  );

  // Wait for the session check: a signed-in buyer's cart needs the Bearer token, and the restore also merges a guest cart.
  React.useEffect(() => {
    if (status === 'loading') return;
    void fetchCart();
  }, [status, fetchCart]);

  const clearDraft = (variantId: string) =>
    setDrafts((d) => {
      if (!(variantId in d)) return d;
      const rest = { ...d };
      delete rest[variantId];
      return rest;
    });

  const commit = (line: CartLine, text: string) => {
    const timer = timers.current.get(line.variantId);
    if (timer !== undefined) window.clearTimeout(timer);
    timers.current.delete(line.variantId);
    const raw = Number(text);
    // Empty or non-numeric input goes back to the server's quantity; a valid one is snapped to MOQ / pack as a hint.
    if (text.trim() === '' || !Number.isFinite(raw)) {
      clearDraft(line.variantId);
      return;
    }
    const next = clampCartQuantity(raw, line);
    if (next === line.quantity) {
      clearDraft(line.variantId);
      return;
    }
    setDrafts((d) => ({ ...d, [line.variantId]: String(next) }));
    void run((api) => api.setItem(line.variantId, next), t('updatedStatus')).finally(() => clearDraft(line.variantId));
  };

  const schedule = (line: CartLine, text: string) => {
    setDrafts((d) => ({ ...d, [line.variantId]: text }));
    const old = timers.current.get(line.variantId);
    if (old !== undefined) window.clearTimeout(old);
    timers.current.set(line.variantId, window.setTimeout(() => commit(line, text), DEBOUNCE_MS));
  };

  const onStep = (line: CartLine, direction: 1 | -1) => {
    const base = Number(drafts[line.variantId] ?? line.quantity);
    const next = stepCartQuantity(Number.isFinite(base) ? base : line.quantity, direction, line);
    if (next !== null) schedule(line, String(next));
  };

  const onRemove = (line: CartLine) => {
    const old = timers.current.get(line.variantId);
    if (old !== undefined) window.clearTimeout(old);
    timers.current.delete(line.variantId);
    clearDraft(line.variantId);
    void run((api) => api.removeItem(line.variantId), t('removedStatus')).then(() => headingRef.current?.focus());
  };

  const busy = pending > 0;
  const live = (
    <p role="status" className="sr-only">
      {busy ? t('updating') : statusText}
    </p>
  );

  if (loadError) {
    return (
      <div role="alert" className="rounded-lg border border-fault/40 bg-fault/10 p-6 text-center">
        <p className="mb-1 font-semibold text-text">{loadError === 'unavailable' ? t('errorUnavailable') : t('errorGeneric')}</p>
        <Button
          type="button"
          variant="secondary"
          className="mt-3"
          onClick={() => {
            setLoadError(null);
            void fetchCart();
          }}
        >
          {t('retry')}
        </Button>
      </div>
    );
  }

  if (!cart) {
    return (
      <>
        <p role="status" className="sr-only">{t('loading')}</p>
        <CartSkeleton />
      </>
    );
  }

  if (cart.lines.length === 0 && cart.notices.length === 0) {
    return (
      <>
        {live}
        {actionError && <ActionError kind={actionError} t={t} />}
        <EmptyState
          title={t('emptyTitle')}
          headingLevel={2}
          description={t('emptyHint')}
          action={
            <Link prefetch={false} href={`${prefix}/search`} className="inline-flex h-10 items-center rounded-md bg-accent px-4 text-sm font-semibold text-accent-fg hover:bg-accent-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent">
              {t('emptyAction')}
            </Link>
          }
        />
      </>
    );
  }

  const { byVariant, orphans } = groupNotices(cart);
  const canCheckout = canProceedToCheckout(cart);
  const blocked = blockedVariantIds(cart).size > 0;

  return (
    <>
      {live}
      <div ref={headingRef} tabIndex={-1} className="outline-none" />
      {actionError && <ActionError kind={actionError} t={t} />}
      {orphans.length > 0 && (
        <div className="mb-4 space-y-2">
          {orphans.map((n, i) => (
            <Notice key={`${n.kind}-${n.variantId}-${i}`} notice={n} currency={cart.currency} locale={locale} t={t} />
          ))}
        </div>
      )}
      <div className="lg:grid lg:grid-cols-[1fr_20rem] lg:gap-8">
        <section aria-busy={busy} aria-label={t('title')} className={busy ? 'opacity-70 transition-opacity' : 'transition-opacity'}>
          <div aria-hidden="true" className="hidden grid-cols-[minmax(0,1fr)_12rem_8rem_8rem_auto] gap-x-4 rounded-t-lg border border-b-0 border-surface-border bg-bg-muted px-4 py-3 text-xs font-semibold uppercase tracking-wide text-text-muted md:grid">
            <span>{t('colProduct')}</span>
            <span>{t('colQuantity')}</span>
            <span>{t('colUnitPrice')}</span>
            <span>{t('colTotal')}</span>
            <span className="w-16" />
          </div>
          <ul className="rounded-lg border border-surface-border md:rounded-t-none">
            {cart.lines.map((line) => (
              <CartLineRow
                key={line.variantId}
                line={line}
                draft={drafts[line.variantId]}
                notices={byVariant.get(line.variantId) ?? []}
                currency={cart.currency}
                locale={locale}
                disabled={busy}
                t={t}
                onDraft={schedule}
                onStep={onStep}
                onCommit={(l) => commit(l, drafts[l.variantId] ?? String(l.quantity))}
                onRemove={onRemove}
              />
            ))}
          </ul>
        </section>

        <aside aria-labelledby="cart-summary" className="mt-6 h-fit rounded-lg border border-surface-border bg-bg-muted p-4 lg:mt-0">
          <h2 id="cart-summary" className="font-display text-lg font-bold text-text">
            {t('summary')}
          </h2>
          <p className="mt-1 text-sm text-text-muted">{t('items', { count: cart.lineCount })}</p>
          <p className="mt-4 flex items-baseline justify-between gap-2">
            <span className="text-sm text-text-muted">{t('subtotal')}</span>
            <span className="text-2xl font-bold text-text">{formatPriceMinor(cart.subtotalMinor, cart.currency, locale)}</span>
          </p>
          <p className="mt-1 text-xs text-text-muted">{t('subtotalNote')}</p>
          {canCheckout && !busy ? (
            <Link
              prefetch={false}
              href={`${prefix}/checkout`}
              className="mt-4 flex h-12 w-full items-center justify-center rounded-md bg-accent px-6 text-base font-semibold text-accent-fg hover:bg-accent-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-2 focus-visible:ring-offset-bg"
            >
              {t('checkout')}
            </Link>
          ) : (
            <>
              <span
                aria-disabled="true"
                aria-describedby={blocked ? 'cart-blocked' : undefined}
                className="mt-4 flex h-12 w-full cursor-not-allowed items-center justify-center rounded-md bg-accent px-6 text-base font-semibold text-accent-fg opacity-50"
              >
                {t('checkout')}
              </span>
              {blocked && (
                <p id="cart-blocked" className="mt-2 text-xs text-text">
                  {t('checkoutBlocked')}
                </p>
              )}
            </>
          )}
          <Link prefetch={false} href={`${prefix}/search`} className="mt-3 block text-center text-sm text-accent underline-offset-4 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent">
            {t('continueShopping')}
          </Link>
        </aside>
      </div>
    </>
  );
}

function ActionError({ kind, t }: { kind: CartErrorKind; t: Translate }) {
  return (
    <p role="alert" className="mb-4 rounded-md border border-fault/40 bg-fault/10 px-4 py-3 text-sm text-text">
      {t(actionErrorKey(kind))}
    </p>
  );
}
