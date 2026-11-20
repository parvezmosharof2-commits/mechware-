'use client';

import * as React from 'react';
import { useRouter } from 'next/navigation';
import { useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import {
  acceptFingerprint, attemptFor, classifyQuoteError, normalisePurchaseOrderNumber, quotePaymentChoices, purchaseOrderNumberProblem, quoteKeepKeyAfter,
  quoteNeedsReload, type AcceptPreview, type BuyerQuoteVersion, type CostCentreDto, type PlaceAttempt, type QuoteChoice, type QuoteErrorKind, type SiteDto
} from '@mechware/api-client';
import { buyerCreditApi, companiesApi } from '../../lib/api';
import { buyerQuotesApi } from '../../lib/quotes-client';
import { useLocalePath } from '../company/shared';
import { useNumberFormat, useQuoteErrorText, useQuoteMoney } from './parts';

const field = 'h-11 w-full rounded-md border border-surface-border-strong bg-bg-muted px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent';

/** Accept in two steps: choose delivery and payment, then confirm the previewed total. The server prices both; this only carries the choice. */
export function QuoteAcceptPanel({ companyId, quoteId, version, onStale }: { companyId: string; quoteId: string; version: BuyerQuoteVersion; onStale: () => void }) {
  const t = useTranslations('BuyerQuotes');
  const fmt = useNumberFormat();
  const money = useQuoteMoney();
  const errorText = useQuoteErrorText();
  const router = useRouter();
  const toPath = useLocalePath();
  const fixed = version.shippingMode === 'fixed';
  const pay = quotePaymentChoices(version.paymentTerms);
  const [sites, setSites] = React.useState<SiteDto[]>([]);
  const [centres, setCentres] = React.useState<CostCentreDto[]>([]);
  const [siteId, setSiteId] = React.useState(version.shipToSiteId ?? '');
  const [centreId, setCentreId] = React.useState('');
  const [po, setPo] = React.useState('');
  // On account only when the company really has credit terms (the server refuses NO_CREDIT_TERMS otherwise); until it is known, advance.
  const [creditKnown, setCreditKnown] = React.useState<'loading' | 'yes' | 'no'>('loading');
  const [onCredit, setOnCredit] = React.useState(false);
  const touchedPay = React.useRef(false);
  const [preview, setPreview] = React.useState<AcceptPreview | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [failure, setFailure] = React.useState<QuoteErrorKind | null>(null);
  const [showProblems, setShowProblems] = React.useState(false);
  const working = React.useRef(false);
  const attempt = React.useRef<PlaceAttempt | null>(null);

  React.useEffect(() => {
    let live = true;
    if (!fixed) {
      companiesApi.listSites(companyId).then(
        (r) => {
          if (!live) return;
          setSites(r.data);
          setSiteId((cur) => cur || r.data.find((s) => s.isDefaultDelivery)?.id || (r.data.length === 1 ? r.data[0]!.id : ''));
        },
        () => undefined
      );
    }
    buyerCreditApi.get(companyId).then(
      (c) => {
        if (!live) return;
        const yes = c.limitMinor > 0;
        setCreditKnown(yes ? 'yes' : 'no');
        if (yes && pay.onAccount && !touchedPay.current) setOnCredit(true);
      },
      () => live && setCreditKnown('no')
    );
    companiesApi.listCostCentres(companyId).then((r) => live && setCentres(r.data), () => undefined);
    return () => {
      live = false;
    };
  }, [companyId, fixed, pay.onAccount]);

  const choice: QuoteChoice = {
    versionNo: version.versionNo,
    contentHash: version.contentHash,
    payOnCredit: onCredit,
    shipToSiteId: siteId || null,
    costCentreId: centreId || null,
    purchaseOrderNumber: normalisePurchaseOrderNumber(po) ?? null
  };
  const poProblem = purchaseOrderNumberProblem(po);
  const siteMissing = !fixed && !siteId;

  const run = async (job: () => Promise<void>) => {
    if (working.current) return; // a double click is one request
    working.current = true;
    setBusy(true);
    setFailure(null);
    try {
      await job();
    } finally {
      working.current = false;
      setBusy(false);
    }
  };

  const review = () => {
    setShowProblems(true);
    if (siteMissing || poProblem) return;
    void run(async () => {
      try {
        setPreview(await buyerQuotesApi.acceptPreview(companyId, quoteId, choice));
      } catch (e) {
        setFailure(classifyQuoteError(e));
      }
    });
  };

  const confirm = () => {
    if (!preview) return;
    void run(async () => {
      attempt.current = attemptFor(attempt.current, acceptFingerprint(companyId, quoteId, choice, preview.grandTotalMinor));
      try {
        const order = await buyerQuotesApi.accept(companyId, quoteId, choice, preview.grandTotalMinor, attempt.current.key);
        router.push(toPath(`/account/orders/${encodeURIComponent(order.orderNumber)}`));
      } catch (e) {
        const kind = classifyQuoteError(e);
        if (!quoteKeepKeyAfter(kind)) attempt.current = null;
        setFailure(kind);
        if (kind === 'priceChanged') setPreview(null); // review the new total before confirming again
      }
    });
  };

  const siteLine = fixed
    ? version.shipTo && `${version.shipTo.siteName}, ${version.shipTo.line1}, ${version.shipTo.upazila}, ${version.shipTo.district}`
    : sites.find((s) => s.id === siteId)?.name;
  const notices: Array<'noticeVat' | 'noticeLower' | 'noticeStock'> = [];
  if (preview?.vatRateChanged) notices.push('noticeVat');
  if (preview?.lowerPriceNow) notices.push('noticeLower');
  if (preview?.stockShort) notices.push('noticeStock');

  return (
    <section aria-labelledby="accept-head" data-testid="accept-panel" className="space-y-4 rounded-lg border border-accent/40 bg-bg-muted p-4">
      <h2 id="accept-head" className="text-lg font-bold">{preview ? t('confirmHeading') : t('acceptHeading')}</h2>
      {failure && (
        <div role="alert" data-testid="accept-error" data-kind={failure} className="space-y-2 rounded-md border border-fault/40 bg-fault/10 px-4 py-3 text-sm">
          <p>{errorText(failure)}</p>
          {quoteNeedsReload(failure) && failure !== 'priceChanged' && <Button variant="outline" size="sm" onClick={onStale}>{t('reloadQuote')}</Button>}
        </div>
      )}
      {!preview ? (
        <div className="space-y-4">
          <fieldset className="space-y-2">
            <legend className="text-sm font-semibold">{t('deliveryHeading')}</legend>
            {fixed ? (
              <p className="text-sm" data-testid="accept-fixed-site">{t('deliveryFixed', { site: siteLine ?? '' })}</p>
            ) : (
              <>
                <label htmlFor="acc-site" className="sr-only">{t('deliverySite')}</label>
                <select id="acc-site" data-testid="accept-site" value={siteId} onChange={(e) => setSiteId(e.target.value)} aria-invalid={showProblems && siteMissing} aria-describedby={showProblems && siteMissing ? 'acc-site-err' : undefined} className={field}>
                  <option value="">{t('deliveryChoose')}</option>
                  {sites.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
                </select>
                {showProblems && siteMissing && <p id="acc-site-err" className="text-sm text-fault">{t('siteRequired')}</p>}
              </>
            )}
          </fieldset>
          <fieldset className="space-y-2">
            <legend className="text-sm font-semibold">{t('paymentHeading')}</legend>
            {pay.onAccount && creditKnown === 'yes' && (
              <label className="flex min-h-11 items-center gap-3 text-sm"><input type="radio" name="acc-pay" checked={onCredit} onChange={() => { touchedPay.current = true; setOnCredit(true); }} data-testid="accept-pay-account" />{t('payAccount')}</label>
            )}
            <label className="flex min-h-11 items-center gap-3 text-sm"><input type="radio" name="acc-pay" checked={!onCredit} onChange={() => { touchedPay.current = true; setOnCredit(false); }} data-testid="accept-pay-advance" />{t('payAdvance')}</label>
            {!pay.onAccount && <p className="text-xs text-text-muted">{t('advanceOnly')}</p>}
            {pay.onAccount && creditKnown === 'no' && <p className="text-xs text-text-muted" data-testid="accept-no-credit">{t('noCreditNote')}</p>}
          </fieldset>
          {centres.length > 0 && (
            <div>
              <label htmlFor="acc-cc" className="mb-1 block text-sm font-semibold">{t('costCentre')}</label>
              <select id="acc-cc" value={centreId} onChange={(e) => setCentreId(e.target.value)} className={field}>
                <option value="">{t('costCentreNone')}</option>
                {centres.map((c) => <option key={c.id} value={c.id}>{c.code} - {c.name}</option>)}
              </select>
            </div>
          )}
          <div>
            <label htmlFor="acc-po" className="mb-1 block text-sm font-semibold">{t('poNumber')}</label>
            <input id="acc-po" value={po} onChange={(e) => setPo(e.target.value)} maxLength={40} autoComplete="off" aria-invalid={!!poProblem} aria-describedby={poProblem ? 'acc-po-err' : undefined} className={field} />
            {poProblem && <p id="acc-po-err" className="mt-1 text-sm text-fault">{t(poProblem === 'tooLong' ? 'poTooLong' : 'poBad')}</p>}
          </div>
          <Button onClick={review} isLoading={busy} data-testid="accept-review">{t('reviewOrder')}</Button>
        </div>
      ) : (
        <div className="space-y-4" data-testid="accept-preview">
          <ul className="divide-y divide-surface-border rounded-md border border-surface-border">
            {preview.lines.map((l) => (
              <li key={l.quoteLineId} className="flex flex-wrap justify-between gap-x-4 gap-y-1 p-3 text-sm">
                <span className="min-w-0 break-words">{l.title} <span className="font-mono text-text-muted">({l.sku})</span> x {fmt(l.quantity)}</span>
                <span className="font-semibold">{money(l.totalMinor)}</span>
              </li>
            ))}
          </ul>
          <dl className="space-y-1 text-sm">
            <div className="flex justify-between gap-4"><dt className="text-text-muted">{t('subtotal')}</dt><dd>{money(preview.subtotalMinor)}</dd></div>
            <div className="flex justify-between gap-4"><dt className="text-text-muted">{t('tax')}</dt><dd>{money(preview.taxMinor)}</dd></div>
            <div className="flex justify-between gap-4"><dt className="text-text-muted">{t('shipping')}</dt><dd>{money(preview.shippingMinor)}</dd></div>
            <div className="flex justify-between gap-4 border-t border-surface-border pt-2 text-base font-bold"><dt>{t('grandTotal')}</dt><dd data-testid="accept-total">{money(preview.grandTotalMinor)}</dd></div>
          </dl>
          <p className="text-sm text-text-muted" data-testid="accept-summary">
            {[siteLine, onCredit ? t('payAccount') : t('payAdvance'), po.trim() && `${t('poNumber')}: ${po.trim()}`].filter(Boolean).join(' - ')}
          </p>
          {notices.length > 0 && (
            <ul className="space-y-1 rounded-md border border-status-pending/40 bg-status-pending/10 p-3 text-sm" data-testid="accept-notices">
              {notices.map((k) => <li key={k}>{t(k)}</li>)}
            </ul>
          )}
          <p className="text-xs text-text-muted">{t('confirmNote', { total: money(preview.grandTotalMinor) })}</p>
          <div className="flex flex-wrap gap-3">
            <Button variant="outline" onClick={() => { setPreview(null); setFailure(null); }} disabled={busy}>{t('changeChoice')}</Button>
            <Button onClick={confirm} isLoading={busy} data-testid="accept-confirm">{t('placeOrder', { total: money(preview.grandTotalMinor) })}</Button>
          </div>
        </div>
      )}
    </section>
  );
}
