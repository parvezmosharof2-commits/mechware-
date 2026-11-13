'use client';

import * as React from 'react';
import { useRouter } from 'next/navigation';
import { useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import {
  CompanyRoles,
  canProceedToCheckout,
  classifyCartError,
  guardCheckoutStep,
  hasRole,
  type CartView,
  type CheckoutDestination,
  type CheckoutStep,
  type CostCentreDto,
  type MyCompanyDto,
  type SiteDto,
  type UserAddress
} from '@mechware/api-client';
import { api, companiesApi } from '../../lib/api';
import { useAuth } from '../../lib/auth';
import { announceCart } from '../../lib/cart-events';
import { cartApi } from '../../lib/cart-client';
import { readCheckoutParams, writeCheckoutParams } from '../../lib/checkout-ui';
import { DeliveryStep } from './DeliveryStep';
import { ReviewStep } from './ReviewStep';

/**
 * Checkout steps 1-2 (delivery) and 3 (review). The flow state lives in the URL (step, address / company / site, cost
 * centre, PO number, attempt key), so a refresh or the back button never loses it and nothing is kept in storage. The cart
 * is always the server's: it is read here on every load, and an empty one sends the buyer back to /cart.
 */
export function CheckoutView({ locale }: { locale: 'en' | 'bn' }) {
  const t = useTranslations('Checkout');
  const router = useRouter();
  const { status } = useAuth();
  const prefix = locale === 'bn' ? '/bn' : '';
  const initial = React.useRef(readCheckoutParams(new URLSearchParams(typeof window === 'undefined' ? '' : window.location.search)));
  const [step, setStep] = React.useState<CheckoutStep>(initial.current.step);
  const [dest, setDest] = React.useState<CheckoutDestination>(initial.current.dest);
  const [key, setKey] = React.useState<string | null>(initial.current.key);
  const [cart, setCart] = React.useState<CartView | null>(null);
  const [loadError, setLoadError] = React.useState(false);
  const [addresses, setAddresses] = React.useState<UserAddress[] | null>(null);
  const [companies, setCompanies] = React.useState<MyCompanyDto[] | null>(null);
  const [sites, setSites] = React.useState<SiteDto[] | null>(null);
  const [costCentres, setCostCentres] = React.useState<CostCentreDto[]>([]);
  const [dataFailed, setDataFailed] = React.useState(false);

  const toLogin = React.useCallback(() => {
    router.replace(`${prefix}/auth/login?returnTo=${encodeURIComponent(`${window.location.pathname}${window.location.search}`)}`);
  }, [router, prefix]);

  // Signed-out visitors go to sign-in and come back here (the middleware does the same before render; this covers an expired session).
  React.useEffect(() => {
    if (status === 'anonymous') toLogin();
  }, [status, toLogin]);

  const loadCart = React.useCallback(async () => {
    setLoadError(false);
    try {
      const next = await cartApi.get();
      setCart(next);
      announceCart(next.lineCount);
    } catch (error) {
      if (classifyCartError(error) === 'forbidden') return toLogin();
      setLoadError(true);
    }
  }, [toLogin]);

  const loadAccount = React.useCallback(async () => {
    setDataFailed(false);
    try {
      const [mine, book] = await Promise.all([companiesApi.listMine().catch(() => ({ data: [] as MyCompanyDto[] })), api.getAddresses()]);
      // Only active companies the user may buy for; an unverified one would be refused at placement anyway.
      const usable = mine.data.filter((m) => m.company.status === 'active' && hasRole(m.roles, CompanyRoles.Buyer));
      setCompanies(usable);
      setAddresses(book.data);
      setDest((d) => {
        if (d.companyId) return usable.some((m) => m.company.id === d.companyId) ? d : { addressId: null };
        return d.addressId && book.data.some((a) => a.id === d.addressId)
          ? d
          : { addressId: book.data.find((a) => a.isDefaultShipping)?.id ?? book.data[0]?.id ?? null };
      });
    } catch {
      setDataFailed(true);
      setCompanies([]);
      setAddresses([]);
    }
  }, []);

  React.useEffect(() => {
    if (status !== 'authenticated') return;
    void loadCart();
    void loadAccount();
  }, [status, loadCart, loadAccount]);

  // The company's sites and cost centres, whenever a company is chosen.
  const companyId = dest.companyId ?? null;
  React.useEffect(() => {
    if (!companyId) return;
    let live = true;
    setSites(null);
    setCostCentres([]);
    companiesApi.listSites(companyId).then(
      (r) => {
        if (!live) return;
        setSites(r.data);
        setDest((d) => (d.companyId === companyId && !d.siteId ? { ...d, siteId: r.data.find((s) => s.isDefaultDelivery)?.id ?? (r.data.length === 1 ? r.data[0].id : null) } : d));
      },
      () => live && (setSites([]), setDataFailed(true))
    );
    // Not every member may list cost centres; without them the field is simply not offered (the server decides if one is needed).
    companiesApi.listCostCentres(companyId).then((r) => live && setCostCentres(r.data), () => undefined);
    return () => { live = false; };
  }, [companyId]);

  // Keep the URL in step with the flow (replace: no history clutter). Only once the account data has decided the defaults.
  const ready = companies !== null && addresses !== null;
  React.useEffect(() => {
    if (!ready) return;
    const qs = writeCheckoutParams({ step, dest, key });
    window.history.replaceState(null, '', `${window.location.pathname}${qs ? `?${qs}` : ''}`);
  }, [ready, step, dest, key]);

  const blocked = cart ? !canProceedToCheckout(cart) && cart.lines.length > 0 : false;
  const decision = guardCheckoutStep(step, cart && ready ? { lineCount: cart.lines.length, blocked } : null, dest);
  const leaveForCart = 'redirect' in decision;
  const allowedStep = 'step' in decision ? decision.step : null;
  React.useEffect(() => {
    if (leaveForCart) router.replace(`${prefix}/cart`);
    else if (allowedStep && allowedStep !== step) setStep(allowedStep);
  }, [leaveForCart, allowedStep, step, router, prefix]);

  const destinationText = React.useMemo(() => {
    if (dest.companyId) {
      const s = sites?.find((x) => x.id === dest.siteId);
      return s ? `${s.name}: ${[s.line1, s.line2, s.upazila, s.district, s.division].filter(Boolean).join(', ')}` : '';
    }
    const a = addresses?.find((x) => x.id === dest.addressId);
    return a ? `${a.label}: ${a.recipientName}, ${a.phone}, ${[a.line1, a.line2, a.upazila, a.district, a.division].filter(Boolean).join(', ')}` : '';
  }, [dest, sites, addresses]);

  if (loadError) {
    return (
      <div role="alert" className="rounded-lg border border-fault/40 bg-fault/10 p-6 text-center">
        <p className="mb-1 font-semibold text-text">{t('errorUnavailable')}</p>
        <Button type="button" variant="secondary" className="mt-3" onClick={() => void loadCart()}>{t('retry')}</Button>
      </div>
    );
  }

  if (status !== 'authenticated' || !cart || !ready || 'wait' in decision || 'redirect' in decision) {
    return <p role="status" className="py-10 text-center text-text-muted">{t('loading')}</p>;
  }

  const current = decision.step;
  const steps: { id: CheckoutStep; label: string }[] = [
    { id: 'delivery', label: t('stepDelivery') },
    { id: 'review', label: t('stepReview') }
  ];
  return (
    <>
      <ol aria-label={t('stepsLabel')} className="mb-6 flex flex-wrap gap-x-6 gap-y-2 text-sm">
        {steps.map((s, i) => (
          <li key={s.id} aria-current={s.id === current ? 'step' : undefined} className={s.id === current ? 'font-semibold text-text' : 'text-text-muted'}>
            {i + 1}. {s.label}
          </li>
        ))}
      </ol>
      {current === 'delivery' ? (
        <DeliveryStep
          locale={locale}
          dest={dest}
          onDest={(next) => {
            setKey(null); // a changed destination is a different request, so it gets a new Idempotency-Key
            setDest(next);
          }}
          addresses={addresses}
          companies={companies}
          sites={sites}
          costCentres={costCentres}
          loadFailed={dataFailed}
          blocked={blocked}
          onAddressAdded={(a) => setAddresses((list) => [...(list ?? []), a])}
          onContinue={() => setStep('review')}
        />
      ) : (
        <ReviewStep
          locale={locale}
          cart={cart}
          dest={dest}
          destinationText={destinationText}
          initialKey={key}
          onKeyChange={setKey}
          onCartRefreshed={(next) => {
            setCart(next);
            announceCart(next.lineCount);
          }}
          onEdit={() => setStep('delivery')}
          onSignIn={toLogin}
        />
      )}
    </>
  );
}
