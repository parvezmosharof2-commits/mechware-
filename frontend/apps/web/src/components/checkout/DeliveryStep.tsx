'use client';

import * as React from 'react';
import Link from 'next/link';
import { useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import {
  PO_NUMBER_MAX,
  isDestinationComplete,
  purchaseOrderNumberProblem,
  type CheckoutDestination,
  type CostCentreDto,
  type MyCompanyDto,
  type SiteDto,
  type UserAddress
} from '@mechware/api-client';
import { AddressForm } from './AddressForm';

export interface DeliveryStepProps {
  locale: 'en' | 'bn';
  dest: CheckoutDestination;
  onDest: (next: CheckoutDestination) => void;
  addresses: UserAddress[] | null;
  companies: MyCompanyDto[];
  sites: SiteDto[] | null;
  costCentres: CostCentreDto[];
  loadFailed: boolean;
  blocked: boolean;
  onAddressAdded: (address: UserAddress) => void;
  onContinue: () => void;
}

const radioCard = 'flex cursor-pointer gap-3 rounded-lg border border-surface-border p-4 has-[:checked]:border-accent has-[:checked]:bg-bg-muted';
const radioInput = 'mt-1 h-4 w-4 shrink-0 accent-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent';

export function DeliveryStep({ locale, dest, onDest, addresses, companies, sites, costCentres, loadFailed, blocked, onAddressAdded, onContinue }: DeliveryStepProps) {
  const t = useTranslations('Checkout');
  const [adding, setAdding] = React.useState(false);
  const [showErrors, setShowErrors] = React.useState(false);
  const prefix = locale === 'bn' ? '/bn' : '';
  const isCompany = !!dest.companyId;
  const poProblem = purchaseOrderNumberProblem(dest.poNumber);
  const complete = isDestinationComplete(dest);

  const chooseBuyer = (companyId: string | null) => {
    const defaultAddress = addresses?.find((a) => a.isDefaultShipping)?.id ?? addresses?.[0]?.id ?? null;
    onDest(companyId ? { companyId, addressId: null, siteId: null, costCentreId: null, poNumber: '' } : { companyId: null, addressId: defaultAddress });
    setShowErrors(false);
  };

  const go = () => {
    if (!complete || blocked) {
      setShowErrors(true);
      return;
    }
    onContinue();
  };

  return (
    <div className="space-y-8">
      {companies.length > 0 && (
        <fieldset className="space-y-3">
          <legend className="font-display text-lg font-bold text-text">{t('buyingAs')}</legend>
          <div className="grid gap-3 md:grid-cols-2">
            <label className={radioCard}>
              <input type="radio" name="buyer" className={radioInput} checked={!isCompany} onChange={() => chooseBuyer(null)} />
              <span className="font-semibold text-text">{t('personalOrder')}</span>
            </label>
            {companies.map((c) => (
              <label key={c.company.id} className={radioCard}>
                <input type="radio" name="buyer" className={radioInput} checked={dest.companyId === c.company.id} onChange={() => chooseBuyer(c.company.id)} />
                <span className="min-w-0 break-words font-semibold text-text">{t('companyOrder', { name: c.company.legalName })}</span>
              </label>
            ))}
          </div>
        </fieldset>
      )}

      {loadFailed && <p role="alert" className="rounded-md border border-fault/40 bg-fault/10 px-4 py-3 text-sm text-text">{t('errorUnavailable')}</p>}

      {!isCompany && (
        <section aria-labelledby="co-address" className="space-y-3">
          <h2 id="co-address" className="font-display text-lg font-bold text-text">{t('deliveryAddress')}</h2>
          {addresses === null && !loadFailed && <p className="text-sm text-text-muted">{t('loading')}</p>}
          {addresses && addresses.length === 0 && !adding && <p className="text-sm text-text-muted">{t('noAddresses')}</p>}
          {addresses && addresses.length > 0 && (
            <fieldset className="grid gap-3 md:grid-cols-2">
              <legend className="sr-only">{t('deliveryAddress')}</legend>
              {addresses.map((a) => (
                <label key={a.id} className={radioCard}>
                  <input type="radio" name="address" className={radioInput} checked={dest.addressId === a.id} onChange={() => onDest({ ...dest, addressId: a.id })} />
                  <span className="min-w-0 text-sm text-text">
                    <span className="block break-words font-semibold">{a.label}{a.isDefaultShipping ? ` (${t('defaultTag')})` : ''}</span>
                    <span className="block break-words">{a.recipientName}, <span className="font-mono">{a.phone}</span></span>
                    <span className="block break-words text-text-muted">{[a.line1, a.line2, a.upazila, a.district, a.division].filter(Boolean).join(', ')}</span>
                  </span>
                </label>
              ))}
            </fieldset>
          )}
          {adding ? (
            <AddressForm
              locale={locale}
              onCancel={() => setAdding(false)}
              onSaved={(a) => {
                onAddressAdded(a);
                onDest({ ...dest, addressId: a.id });
                setAdding(false);
              }}
            />
          ) : (
            addresses && <Button type="button" variant="outline" onClick={() => setAdding(true)}>{t('addAddress')}</Button>
          )}
        </section>
      )}

      {isCompany && (
        <section aria-labelledby="co-site" className="space-y-4">
          <h2 id="co-site" className="font-display text-lg font-bold text-text">{t('deliverySite')}</h2>
          {sites === null && !loadFailed && <p className="text-sm text-text-muted">{t('loading')}</p>}
          {sites && sites.length === 0 && (
            <p className="text-sm text-text-muted">
              {t('noSites')}{' '}
              <Link prefetch={false} className="text-accent underline-offset-4 hover:underline" href={`${prefix}/account/company/${dest.companyId}`}>{t('manageCompany')}</Link>
            </p>
          )}
          {sites && sites.length > 0 && (
            <fieldset className="grid gap-3 md:grid-cols-2">
              <legend className="sr-only">{t('deliverySite')}</legend>
              {sites.map((s) => (
                <label key={s.id} className={radioCard}>
                  <input type="radio" name="site" className={radioInput} checked={dest.siteId === s.id} onChange={() => onDest({ ...dest, siteId: s.id })} />
                  <span className="min-w-0 text-sm text-text">
                    <span className="block break-words font-semibold">{s.name}{s.isDefaultDelivery ? ` (${t('defaultTag')})` : ''}</span>
                    <span className="block break-words">{s.contactName}, <span className="font-mono">{s.contactPhone}</span></span>
                    <span className="block break-words text-text-muted">{[s.line1, s.line2, s.upazila, s.district, s.division].filter(Boolean).join(', ')}</span>
                  </span>
                </label>
              ))}
            </fieldset>
          )}
          {costCentres.length > 0 && (
            <div className="flex max-w-md flex-col gap-1.5">
              <label htmlFor="co-cc" className="text-sm font-medium text-text">{t('costCentre')}</label>
              <select
                id="co-cc"
                value={dest.costCentreId ?? ''}
                onChange={(e) => onDest({ ...dest, costCentreId: e.target.value || null })}
                className="h-11 w-full rounded-md border border-surface-border-strong bg-bg-muted px-3 text-sm text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
              >
                <option value="">{t('costCentreNone')}</option>
                {costCentres.map((c) => <option key={c.id} value={c.id}>{c.code} - {c.name}</option>)}
              </select>
            </div>
          )}
          <div className="max-w-md">
            <Input
              label={t('poNumber')}
              helperText={t('poHint', { max: PO_NUMBER_MAX })}
              value={dest.poNumber ?? ''}
              onChange={(e) => onDest({ ...dest, poNumber: e.target.value })}
              error={poProblem === 'tooLong' ? t('poTooLong', { max: PO_NUMBER_MAX }) : poProblem === 'badCharacters' ? t('poBadCharacters') : undefined}
              autoComplete="off"
            />
          </div>
        </section>
      )}

      {blocked && <p role="alert" className="rounded-md border border-fault/40 bg-fault/10 px-4 py-3 text-sm text-text">{t('cartBlocked')} <Link prefetch={false} className="font-semibold underline" href={`${prefix}/cart`}>{t('backToCart')}</Link></p>}
      {showErrors && !complete && !blocked && <p role="alert" className="text-sm text-fault">{isCompany ? t('needSite') : t('needAddress')}</p>}

      <div className="flex flex-wrap items-center gap-4">
        <Button type="button" variant="accent" size="lg" onClick={go}>{t('continueToReview')}</Button>
        <Link prefetch={false} className="text-sm text-accent underline-offset-4 hover:underline" href={`${prefix}/cart`}>{t('backToCart')}</Link>
      </div>
    </div>
  );
}
