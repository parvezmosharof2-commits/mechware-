'use client';

import * as React from 'react';
import Link from 'next/link';
import { useTranslations } from 'next-intl';
import { CART_EVENT, badgeText, type CartChangedDetail } from '../../lib/cart-events';
import { useAuth } from '../../lib/auth';

/**
 * Cart link with an item-count badge. The link is in the first paint at a fixed size; the count is fetched when the page
 * is idle (and after the session is known, so a signed-in buyer's cart is read, not the guest's) and is drawn in an
 * absolutely positioned badge, so it never moves anything.
 */
export function HeaderCart({ locale }: { locale: 'en' | 'bn' }) {
  const t = useTranslations('Layout');
  const { status } = useAuth();
  const [count, setCount] = React.useState(0);

  React.useEffect(() => {
    if (status === 'loading') return;
    let cancelled = false;
    const w = window as Window & { requestIdleCallback?: (cb: () => void, o?: { timeout: number }) => number; cancelIdleCallback?: (h: number) => void };
    const load = () => {
      import('../../lib/cart-client')
        .then((m) => m.cartApi.get())
        .then(
          (cart) => !cancelled && setCount(cart.lineCount),
          () => undefined // the badge is a hint: on failure it stays as it was (the cart page reports errors)
        );
    };
    // Non-critical: wait for the page's own load, then a short pause, then an idle moment, so the count never competes with the first interaction.
    let idle: number | undefined;
    let timer: number | undefined;
    const schedule = () => {
      timer = window.setTimeout(() => {
        if (w.requestIdleCallback) idle = w.requestIdleCallback(load, { timeout: 3000 });
        else load();
      }, 1500);
    };
    if (document.readyState === 'complete') schedule();
    else window.addEventListener('load', schedule, { once: true });
    return () => {
      cancelled = true;
      window.removeEventListener('load', schedule);
      if (timer !== undefined) window.clearTimeout(timer);
      if (idle !== undefined) w.cancelIdleCallback?.(idle);
    };
  }, [status]);

  React.useEffect(() => {
    const on = (e: Event) => setCount((e as CustomEvent<CartChangedDetail>).detail.lineCount);
    window.addEventListener(CART_EVENT, on);
    return () => window.removeEventListener(CART_EVENT, on);
  }, []);

  return (
    <Link
      prefetch={false}
      href={locale === 'bn' ? '/bn/cart' : '/cart'}
      aria-label={count > 0 ? t('cartItems', { count }) : t('cart')}
      className="relative flex h-10 w-10 items-center justify-center rounded-md border border-surface-border bg-bg-subtle text-text hover:bg-surface focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
    >
      <svg aria-hidden="true" viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <circle cx="9" cy="20" r="1.5" />
        <circle cx="18" cy="20" r="1.5" />
        <path d="M2 3h3l2.7 12.4a1 1 0 0 0 1 .8h9.1a1 1 0 0 0 1-.8L21 7H6" />
      </svg>
      {count > 0 && (
        <span aria-hidden="true" className="absolute -right-1.5 -top-1.5 flex h-5 min-w-5 items-center justify-center rounded-full bg-accent px-1 text-[11px] font-bold leading-none text-accent-fg">
          {badgeText(count, locale)}
        </span>
      )}
    </Link>
  );
}
