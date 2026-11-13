'use client';

import * as React from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import {
  attemptFingerprint,
  attemptFor,
  buildOrderBody,
  buildQuoteBody,
  canProceedToCheckout,
  cartChangedSince,
  classifyCheckoutError,
  formatPriceMinor,
  keepKeyAfter,
  problemMessage,
  shouldRefreshCartAfter,
  type CartView,
  type CheckoutDestination,
  type CheckoutErrorKind,
  type PlaceAttempt,
  type ShippingQuote
} from '@mechware/api-client';
import { announceCart } from '../../lib/cart-events';
import { cartApi } from '../../lib/cart-client';
import { ordersApi } from '../../lib/checkout-client';
import { checkoutErrorKey, skusFromError } from '../../lib/checkout-ui';

type Quote = { state: 'loading' } | { state: 'ok'; data: ShippingQuote } | { state: 'error'; kind: CheckoutErrorKind; skus: string[] };
type Translate = ReturnType<typeof useTranslations>;

export interface ReviewStepProps {
  locale: 'en' | 'bn';
  cart: CartView;
  dest: CheckoutDestination;
  /** Where the order goes, as text (address or site), for the "Delivering to" line. */
  destinationText: string;
  /** Idempotency-Key from the URL (a refresh during an attempt), adopted for the first placement. */
  initialKey: string | null;
  onKeyChange: (key: string | null) => void;
  onCartRefreshed: (cart: CartView) => void;
  onEdit: () => void;
  onSignIn: () => void;
}

function ErrorBox({ kind, skus, detail, t }: { kind: CheckoutErrorKind; skus: string[]; detail?: string; t: Translate }) {
  return (
    <div role="alert" className="rounded-md border border-fault/40 bg-fault/10 px-4 py-3 text-sm text-text">
      <p>{t(checkoutErrorKey(kind))}</p>
      {kind === 'quoteOnly' && <p className="mt-1">{t('quoteOnlyHint')}</p>}
      {skus.length > 0 && (kind === 'quoteOnly' || kind === 'weightMissing') && <p className="mt-1">{t('affectedItems')}: <span className="font-mono break-all">{skus.join(', ')}</span></p>}
      {kind === 'invalid' && detail && <p className="mt-1 break-words">{detail}</p>}
    </div>
  );
}

export function ReviewStep({ locale, cart, dest, destinationText, initialKey, onKeyChange, onCartRefreshed, onEdit, onSignIn }: ReviewStepProps) {
  const t = useTranslations('Checkout');
  const router = useRouter();
  const prefix = locale === 'bn' ? '/bn' : '';
  const money = (minor: number) => formatPriceMinor(minor, cart.currency, locale);
  const [quote, setQuote] = React.useState<Quote>({ state: 'loading' });
  const [quoteRun, setQuoteRun] = React.useState(0);
  const [placing, setPlacing] = React.useState(false);
  const [problem, setProblem] = React.useState<{ kind: CheckoutErrorKind; skus: string[]; detail?: string } | null>(null);
  const [changed, setChanged] = React.useState(false);
  const placingRef = React.useRef(false);
  const attemptRef = React.useRef<PlaceAttempt | null>(initialKey ? { key: initialKey, fingerprint: '' } : null);
  const mounted = React.useRef(true);
  React.useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; };
  }, []);

  const lines = cart.lines;
  const quoteBody = JSON.stringify(buildQuoteBody(lines, dest));
  React.useEffect(() => {
    let live = true;
    setQuote({ state: 'loading' });
    ordersApi.quoteShipping(JSON.parse(quoteBody)).then(
      (data) => live && setQuote({ state: 'ok', data }),
      (error) => {
        if (!live) return;
        const kind = classifyCheckoutError(error);
        if (kind === 'signIn') onSignIn();
        setQuote({ state: 'error', kind, skus: skusFromError(error) });
      }
    );
    return () => { live = false; };
  }, [quoteBody, quoteRun, onSignIn]);

  const place = async () => {
    if (placingRef.current || quote.state !== 'ok') return;
    placingRef.current = true;
    setPlacing(true);
    setProblem(null);
    setChanged(false);
    let navigating = false;
    try {
      // The review shows prices from a moment ago: re-read the cart and stop if anything moved, so nobody pays a price they did not see.
      const fresh = await cartApi.get();
      if (cartChangedSince(cart, fresh) || !canProceedToCheckout(fresh)) {
        onCartRefreshed(fresh);
        setChanged(true);
        return;
      }
      const fingerprint = attemptFingerprint(lines, dest);
      let previous = attemptRef.current;
      if (previous && previous.fingerprint === '') previous = { ...previous, fingerprint };
      const attempt = attemptFor(previous, fingerprint);
      attemptRef.current = attempt;
      onKeyChange(attempt.key);
      const placed = await ordersApi.place(buildOrderBody(lines, dest), attempt.key);
      // The order exists now; emptying the cart is best effort and must never block the confirmation.
      void cartApi.clear().then(() => announceCart(0), () => undefined);
      navigating = true; // the button stays disabled while the page changes
      router.push(`${prefix}/checkout/confirmation?order=${encodeURIComponent(placed.orderNumber)}&id=${encodeURIComponent(placed.orderId)}`);
    } catch (error) {
      const kind = classifyCheckoutError(error);
      if (kind === 'signIn') {
        onSignIn();
        return;
      }
      if (!keepKeyAfter(kind)) {
        attemptRef.current = null;
        onKeyChange(null);
      }
      if (shouldRefreshCartAfter(kind)) {
        try { onCartRefreshed(await cartApi.get()); } catch { /* the error below already tells the buyer to retry */ }
      }
      if (mounted.current) setProblem({ kind, skus: skusFromError(error), detail: kind === 'invalid' ? problemMessage(error, '') : undefined });
    } finally {
      if (!navigating) {
        placingRef.current = false;
        if (mounted.current) setPlacing(false);
      }
    }
  };

  const quoteOk = quote.state === 'ok';
  const sellerCount = quote.state === 'ok' ? quote.data.perSeller.length : 0;
  return (
    <div className="lg:grid lg:grid-cols-[1fr_20rem] lg:gap-8">
      <div className="space-y-6">
        <section aria-labelledby="rv-dest" className="rounded-lg border border-surface-border p-4">
          <div className="flex flex-wrap items-start justify-between gap-2">
            <h2 id="rv-dest" className="font-display text-lg font-bold text-text">{t('deliveringTo')}</h2>
            <Button type="button" variant="ghost" size="sm" onClick={onEdit} disabled={placing}>{t('change')}</Button>
          </div>
          <p className="mt-1 break-words text-sm text-text">{destinationText}</p>
          {dest.companyId && dest.poNumber?.trim() && <p className="mt-1 text-sm text-text-muted">{t('poNumber')}: <span className="font-mono break-all">{dest.poNumber.trim()}</span></p>}
        </section>

        <section aria-labelledby="rv-items">
          <h2 id="rv-items" className="mb-2 font-display text-lg font-bold text-text">{t('itemsTitle')}</h2>
          <ul className="rounded-lg border border-surface-border">
            {lines.map((l) => (
              <li key={l.variantId} className="grid gap-x-4 gap-y-1 border-b border-surface-border px-4 py-3 last:border-b-0 md:grid-cols-[minmax(0,1fr)_auto_8rem_8rem] md:items-center">
                <div className="min-w-0">
                  <p className="break-words font-semibold text-text">{l.title}</p>
                  <p className="text-xs text-text-muted">{t('partNumber')}: <span className="break-all font-mono text-text">{l.sku}</span></p>
                </div>
                <p className="text-sm text-text-muted">{t('qty', { count: l.quantity })}</p>
                <p className="text-sm text-text">{money(l.unitPriceMinor)}</p>
                <p className="font-semibold text-text">{money(l.lineTotalMinor)}</p>
              </li>
            ))}
          </ul>
        </section>
      </div>

      <aside aria-labelledby="rv-sum" className="mt-6 h-fit space-y-3 rounded-lg border border-surface-border bg-bg-muted p-4 lg:mt-0">
        <h2 id="rv-sum" className="font-display text-lg font-bold text-text">{t('summary')}</h2>
        <p className="flex items-baseline justify-between gap-2"><span className="text-sm text-text-muted">{t('subtotal')}</span><span className="font-semibold text-text">{money(cart.subtotalMinor)}</span></p>
        {quote.state === 'loading' && <p role="status" className="text-sm text-text-muted">{t('quoting')}</p>}
        {quote.state === 'ok' && (
          <>
            {sellerCount > 1 && quote.data.perSeller.map((s, i) => (
              <p key={s.sellerId} className="flex items-baseline justify-between gap-2 text-sm"><span className="text-text-muted">{t('shippingSeller', { n: i + 1 })}</span><span className="text-text">{money(s.shippingMinor)}</span></p>
            ))}
            <p className="flex items-baseline justify-between gap-2"><span className="text-sm text-text-muted">{t('shipping')}</span><span className="font-semibold text-text">{quote.data.totalMinor === 0 ? t('shippingFree') : money(quote.data.totalMinor)}</span></p>
            {quote.data.freeShippingApplied && <p className="text-xs text-text-muted">{t('freeShippingApplied')}</p>}
          </>
        )}
        <p className="text-xs text-text-muted">{t('finalTotalNote')}</p>
        {quote.state === 'error' && <ErrorBox kind={quote.kind} skus={quote.skus} t={t} />}
        {quote.state === 'error' && (quote.kind === 'unavailable' || quote.kind === 'busy' || quote.kind === 'rateLimited') && (
          <Button type="button" variant="secondary" onClick={() => setQuoteRun((n) => n + 1)}>{t('retry')}</Button>
        )}
        {quote.state === 'error' && ['notDeliverable', 'quoteOnly', 'invalid', 'forbidden', 'notFound'].includes(quote.kind) && (
          <Button type="button" variant="secondary" onClick={onEdit}>{t('changeDelivery')}</Button>
        )}
        {changed && <p role="alert" className="rounded-md border border-status-pending/50 bg-status-pending/10 px-3 py-2 text-sm text-text">{t('cartChanged')} <Link prefetch={false} className="font-semibold underline" href={`${prefix}/cart`}>{t('backToCart')}</Link></p>}
        {problem && <ErrorBox kind={problem.kind} skus={problem.skus} detail={problem.detail} t={t} />}
        {problem && keepKeyAfter(problem.kind) && <p className="text-xs text-text-muted">{t('retrySafe')}</p>}
        <Button type="button" variant="accent" size="lg" className="w-full" disabled={!quoteOk || placing} isLoading={placing} aria-describedby={quoteOk ? undefined : 'rv-why'} onClick={place}>
          {placing ? t('placing') : t('placeOrder')}
        </Button>
        {!quoteOk && <p id="rv-why" className="text-xs text-text-muted">{t('placeDisabled')}</p>}
        <p role="status" className="sr-only">{placing ? t('placing') : ''}</p>
      </aside>
    </div>
  );
}
