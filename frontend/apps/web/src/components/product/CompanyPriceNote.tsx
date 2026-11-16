'use client';

import * as React from 'react';
import { useLocale, useTranslations } from 'next-intl';
import { formatPriceMinor } from '@mechware/api-client';
import { contractLastDay, pickDisplay, tierRows } from '@mechware/api-client/src/company-prices-model';
import { useCompanyPrices } from '../../lib/useCompanyPrices';

/**
 * D2.13: the signed-in company buyer's own price on the product page. It renders nothing (no reserved space) for guests,
 * personal accounts and when the API has no better price; the data is fetched in the browser after hydration and is never
 * part of the server HTML. Company prices are NET of VAT, so every figure here is labelled and the public price is quoted
 * as VAT-inclusive, never mixed.
 */
export default function CompanyPriceNote({ variantId, quantity, currency }: { variantId: string; quantity: number; currency: string }) {
  const t = useTranslations('CompanyPrice');
  const locale = useLocale();
  const asks = React.useMemo(() => [{ variantId, quantity }], [variantId, quantity]);
  const { phase, company, items, refreshing } = useCompanyPrices(asks);
  const item = items.get(variantId.toLowerCase());
  const display = phase === 'ready' ? pickDisplay(item) : null;
  if (!display || display.kind === 'none' || !company) return null;

  const money = (minor: number) => formatPriceMinor(minor, currency, locale);
  const tiers = tierRows(item, display.quantity);
  const lastDay = contractLastDay(display.validToUtc);
  const dateText = lastDay
    ? new Intl.DateTimeFormat(locale === 'bn' ? 'bn-BD' : 'en-GB', { dateStyle: 'medium', timeZone: 'UTC' }).format(new Date(`${lastDay}T00:00:00Z`))
    : null;
  const num = (n: number) => new Intl.NumberFormat(locale === 'bn' ? 'bn-BD' : 'en-US').format(n);

  return (
    <section
      aria-label={t(display.kind === 'contract' ? 'yourContractPrice' : 'yourPrice')}
      aria-busy={refreshing}
      data-testid="company-price"
      className={`mt-3 border-t border-surface-border pt-3 ${refreshing ? 'opacity-70' : ''}`}
    >
      <p className="text-sm font-medium text-text">
        {t(display.kind === 'contract' ? 'yourContractPrice' : 'yourPrice')}
        {display.contractRef && <span className="font-normal text-text-muted"> ({t('ref', { ref: display.contractRef })})</span>}
      </p>
      <p className="mt-1 flex flex-wrap items-baseline gap-x-2">
        <span className="text-2xl font-bold text-text" data-testid="company-price-unit">{money(display.unitNetMinor)}</span>
        <span className="text-sm text-text-muted">{t('exclVat')}</span>
      </p>
      <p className="mt-1 text-xs text-text-muted">
        {t('vatLine', { percent: num(display.vatPercent), vat: money(display.lineTaxMinor), count: display.quantity, countText: num(display.quantity), total: money(display.lineTotalMinor) })}
      </p>
      {display.guestUnitInclVatMinor !== undefined && (
        <p className="mt-1 text-xs text-text-muted">{t('publicPrice', { price: money(display.guestUnitInclVatMinor) })}</p>
      )}
      {dateText && <p className="mt-1 text-xs text-text-muted">{t('validUntil', { date: dateText })}</p>}
      <p className="mt-1 text-xs text-text-muted">{t('forCompany', { company: company.name })}</p>

      {tiers.length > 0 && (
        <table className="mt-3 w-full max-w-sm text-left text-sm">
          <caption className="mb-1 text-left text-xs font-medium text-text-muted">{t('tiersCaption')}</caption>
          <thead>
            <tr className="text-xs text-text-muted">
              <th scope="col" className="py-1 pr-3 font-medium">{t('tierQty')}</th>
              <th scope="col" className="py-1 font-medium">{t('tierPrice')}</th>
            </tr>
          </thead>
          <tbody>
            {tiers.map((row) => (
              <tr key={row.minQty} className={row.current ? 'font-semibold text-text' : 'text-text-muted'} aria-current={row.current ? 'true' : undefined}>
                <th scope="row" className="py-1 pr-3 font-normal">{t('tierFrom', { min: num(row.minQty) })}</th>
                <td className="py-1">
                  {money(row.unitNetMinor)}
                  {row.current && <span className="sr-only"> ({t('currentTier')})</span>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
