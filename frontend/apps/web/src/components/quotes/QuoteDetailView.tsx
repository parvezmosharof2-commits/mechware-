'use client';

import * as React from 'react';
import Link from 'next/link';
import { useLocale, useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@mechware/ui/src/components/Dialog';
import {
  QUOTE_REJECT_MAX, canRejectVersion, classifyQuoteError, classifyQuotePdfError, lineTitle, mayAccept, newestAcceptable, newestVersion, quotedTotal, quoteStatusKey,
  type BuyerQuoteDetail, type BuyerQuoteVersion, type QuoteErrorKind, type QuotePdfErrorKind
} from '@mechware/api-client';
import { companiesApi } from '../../lib/api';
import { useAuth } from '../../lib/auth';
import { ordersApi } from '../../lib/checkout-client';
import { buyerQuotesApi, downloadQuotePdf } from '../../lib/quotes-client';
import { useResource } from '../../lib/useResource';
import { ListSkeleton, PageShell, RequireSignIn, useLocalePath } from '../company/shared';
import { BuyerQuoteBadge, formatDay, useNumberFormat, useQuoteErrorText, useQuoteMoney } from './parts';
import { QuoteAcceptPanel } from './QuoteAcceptPanel';

type Loaded = { kind: 'ok'; detail: BuyerQuoteDetail; roles: number; orderNumber: string | null } | { kind: 'notFound' };

async function load(companyId: string, quoteId: string): Promise<Loaded> {
  try {
    const [detail, mine] = await Promise.all([buyerQuotesApi.get(companyId, quoteId), companiesApi.listMine().catch(() => null)]);
    const roles = mine?.data.find((m) => m.company.id.toLowerCase() === companyId.toLowerCase())?.roles ?? 0;
    const orderId = newestVersion(detail)?.acceptedOrderId;
    const orderNumber = orderId ? await ordersApi.get(orderId).then((o) => o.orderNumber, () => null) : null;
    return { kind: 'ok', detail, roles, orderNumber };
  } catch (e) {
    if (classifyQuoteError(e) === 'notFound') return { kind: 'notFound' };
    throw e;
  }
}

function VersionBody({ v, locale }: { v: BuyerQuoteVersion; locale: 'en' | 'bn' }) {
  const t = useTranslations('BuyerQuotes');
  const fmt = useNumberFormat();
  const money = useQuoteMoney();
  return (
    <div className="space-y-4">
      <ul className="divide-y divide-surface-border rounded-lg border border-surface-border bg-bg-muted" data-testid="quote-lines">
        {v.lines.map((l) => (
          <li key={l.id} className="space-y-1 p-4 text-sm">
            <div className="flex flex-wrap justify-between gap-x-4 gap-y-1">
              <span className="min-w-0 break-words font-medium">{lineTitle(l, locale)} <span className="font-mono text-text-muted">({l.sku})</span></span>
              <span className="font-semibold">{money(l.lineTotalMinor)}</span>
            </div>
            <p className="text-text-muted">{t('lineQty', { qty: fmt(l.quantity), price: money(l.unitPriceMinor) })}</p>
            {l.lineNote && <p className="text-xs text-text-muted">{l.lineNote}</p>}
          </li>
        ))}
      </ul>
      <dl className="space-y-1 rounded-lg border border-surface-border bg-bg-muted p-4 text-sm">
        <div className="flex justify-between gap-4"><dt className="text-text-muted">{t('subtotal')}</dt><dd>{money(v.goodsNetMinor)}</dd></div>
        <div className="flex justify-between gap-4"><dt className="text-text-muted">{t('tax')}</dt><dd>{money(v.taxMinor)}</dd></div>
        <div className="flex justify-between gap-4"><dt className="text-text-muted">{t('shipping')}</dt><dd data-testid="quote-shipping">{v.shippingTotalMinor !== null ? money(v.shippingTotalMinor) : t('shippingAtOrder')}</dd></div>
        <div className="flex justify-between gap-4 border-t border-surface-border pt-2 text-base font-bold"><dt>{quotedTotal(v).withDelivery ? t('grandTotal') : t('goodsTotal')}</dt><dd data-testid="quote-total">{money(quotedTotal(v).minor)}</dd></div>
      </dl>
      <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
        <div><dt className="text-text-muted">{t('payment')}</dt><dd>{v.paymentTerms === 'advance' ? t('payAdvanceOnly') : t('payAccountOrAdvance')}</dd></div>
        <div>
          <dt className="text-text-muted">{t('delivery')}</dt>
          <dd>{v.shippingMode === 'fixed' && v.shipTo ? `${v.shipTo.siteName}, ${v.shipTo.line1}, ${v.shipTo.upazila}, ${v.shipTo.district}` : t('deliveryChosenLater')}</dd>
        </div>
        {v.preparerName && <div><dt className="text-text-muted">{t('preparedBy')}</dt><dd>{v.preparerName}</dd></div>}
        <div><dt className="text-text-muted">{t('validUntil')}</dt><dd>{formatDay(v.validUntil, locale)}</dd></div>
      </dl>
      {v.buyerNote && <p className="whitespace-pre-line rounded bg-bg-subtle p-3 text-sm" data-testid="quote-note">{v.buyerNote}</p>}
      {v.termsText && (
        <details className="rounded-lg border border-surface-border bg-bg-muted p-4 text-sm">
          <summary className="cursor-pointer font-semibold">{t('terms')}</summary>
          <p className="mt-2 whitespace-pre-line text-text-muted">{v.termsText}</p>
        </details>
      )}
    </div>
  );
}

function Panel({ companyId, quoteId }: { companyId: string; quoteId: string }) {
  const t = useTranslations('BuyerQuotes');
  const locale = useLocale() === 'bn' ? 'bn' : 'en';
  const errorText = useQuoteErrorText();
  const { user } = useAuth();
  const toPath = useLocalePath();
  const result = useResource(() => load(companyId, quoteId), `${user?.id ?? ''}:${companyId}:${quoteId}`);
  const [accepting, setAccepting] = React.useState(false);
  const [rejecting, setRejecting] = React.useState(false);
  const [reason, setReason] = React.useState('');
  const [busy, setBusy] = React.useState(false);
  const [rejected, setRejected] = React.useState<BuyerQuoteDetail | null>(null);
  const [failure, setFailure] = React.useState<QuoteErrorKind | null>(null);
  const [pdfFailed, setPdfFailed] = React.useState<QuotePdfErrorKind | null>(null);
  const working = React.useRef(false);

  if (result.loading && !result.data) return <ListSkeleton rows={4} />;
  if (result.error) {
    return (
      <div role="alert" className="space-y-3 rounded-lg border border-fault/50 bg-fault/10 p-4 text-sm">
        <p>{t('loadError')}</p>
        <Button variant="outline" size="sm" onClick={result.reload}>{t('retry')}</Button>
      </div>
    );
  }
  const r = result.data;
  const back = toPath(`/account/company/${encodeURIComponent(companyId)}/quotes`);
  if (!r || r.kind === 'notFound') {
    return (
      <div data-testid="quote-not-found" className="space-y-2 rounded-lg border border-surface-border bg-bg-muted p-5">
        <h2 className="text-lg font-semibold">{t('notFoundTitle')}</h2>
        <p className="text-sm text-text-muted">{t('notFoundBody')}</p>
        <Link prefetch={false} href={back} className="text-sm font-semibold text-accent hover:underline">{t('backToList')}</Link>
      </div>
    );
  }
  const detail = rejected ?? r.detail;
  const newest = newestVersion(detail);
  if (!newest) return null;
  const acceptable = newestAcceptable(detail);
  const earlier = detail.versions.slice(0, -1).reverse();
  const key = quoteStatusKey(newest.status);

  const reject = async () => {
    if (working.current) return;
    working.current = true;
    setBusy(true);
    setFailure(null);
    try {
      setRejected(await buyerQuotesApi.reject(companyId, quoteId, newest.versionNo, reason));
      setRejecting(false);
      setAccepting(false);
    } catch (e) {
      setFailure(classifyQuoteError(e));
      setRejecting(false);
    } finally {
      working.current = false;
      setBusy(false);
    }
  };

  const pdf = async () => {
    setPdfFailed(null);
    try {
      await downloadQuotePdf(companyId, quoteId, newest.versionNo, locale, `${detail.quote.number}-v${newest.versionNo}.pdf`);
    } catch (e) {
      setPdfFailed(classifyQuotePdfError(e));
    }
  };

  return (
    <div className="space-y-6">
      {failure && <p role="alert" data-testid="quote-error" data-kind={failure} className="rounded-md border border-fault/40 bg-fault/10 px-4 py-3 text-sm">{errorText(failure)}</p>}
      {rejected && <p role="status" data-testid="quote-rejected" className="rounded-md border border-surface-border-strong bg-bg-subtle px-4 py-3 text-sm">{t('rejectedNote')}</p>}

      <section aria-labelledby="q-head" className="rounded-lg border border-surface-border bg-bg-muted p-4">
        <div className="flex flex-wrap items-center gap-3">
          <h2 id="q-head" className="font-mono text-lg font-bold" data-testid="quote-number">{detail.quote.number}</h2>
          <BuyerQuoteBadge status={newest.status} />
          <span className="text-sm text-text-muted">{t('versionNo', { n: newest.versionNo })}</span>
        </div>
        <p className="mt-2 text-sm text-text-muted" data-testid="quote-state-note">{t(`note_${key}` as 'note_other', { date: formatDay(newest.validUntil, locale) })}</p>
        {key === 'accepted' && r.orderNumber && (
          <Link prefetch={false} href={toPath(`/account/orders/${encodeURIComponent(r.orderNumber)}`)} data-testid="quote-order-link" className="mt-2 inline-block text-sm font-semibold text-accent hover:underline">{t('viewOrder', { number: r.orderNumber })}</Link>
        )}
        {newest.rejectReason && <p className="mt-2 rounded bg-bg-subtle p-3 text-sm">{t('yourReason')}: {newest.rejectReason}</p>}
      </section>

      <VersionBody v={newest} locale={locale} />

      <div className="flex flex-wrap items-center gap-3">
        {acceptable && mayAccept(r.roles) && !accepting && <Button onClick={() => setAccepting(true)} data-testid="quote-accept">{t('accept')}</Button>}
        {acceptable && !mayAccept(r.roles) && <p className="text-sm text-text-muted" data-testid="quote-no-accept">{t('onlyBuyerAccepts')}</p>}
        {canRejectVersion(newest) && !rejected && <Button variant="outline" onClick={() => setRejecting(true)} disabled={busy} data-testid="quote-reject">{t('reject')}</Button>}
        <Button variant="outline" onClick={() => void pdf()} data-testid="quote-pdf">{t('downloadPdf')}</Button>
      </div>
      {pdfFailed && <p role="alert" data-testid="quote-pdf-error" data-kind={pdfFailed} className="text-sm text-fault">{t(`pdfError_${pdfFailed}` as 'pdfError_failed')}</p>}

      {accepting && acceptable && <QuoteAcceptPanel companyId={companyId} quoteId={quoteId} version={acceptable} onStale={() => { setAccepting(false); result.reload(); }} />}

      {earlier.length > 0 && (
        <section aria-labelledby="q-earlier">
          <h2 id="q-earlier" className="mb-2 text-lg font-bold">{t('earlierHeading')}</h2>
          <ul className="space-y-2" data-testid="quote-earlier">
            {earlier.map((v) => (
              <li key={v.versionNo}>
                <details className="rounded-lg border border-surface-border bg-bg-muted p-4">
                  <summary className="flex cursor-pointer flex-wrap items-center gap-3 text-sm font-semibold">
                    <span>{t('versionNo', { n: v.versionNo })}</span>
                    <BuyerQuoteBadge status={v.status} />
                  </summary>
                  <div className="mt-3"><VersionBody v={v} locale={locale} /></div>
                </details>
              </li>
            ))}
          </ul>
        </section>
      )}

      <Link prefetch={false} href={back} className="inline-block text-sm font-semibold text-accent hover:underline">{t('backToList')}</Link>

      <Dialog open={rejecting} onOpenChange={(open) => !open && !busy && setRejecting(false)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t('rejectTitle', { number: detail.quote.number })}</DialogTitle>
            <DialogDescription>{t('rejectBody')}</DialogDescription>
          </DialogHeader>
          <div>
            <label htmlFor="rej-reason" className="mb-1 block text-sm font-semibold">{t('rejectLabel')}</label>
            <textarea id="rej-reason" data-testid="reject-reason" value={reason} onChange={(e) => setReason(e.target.value)} rows={4} maxLength={QUOTE_REJECT_MAX} aria-describedby="rej-hint" className="w-full rounded-md border border-surface-border-strong bg-bg-muted p-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent" />
            <p id="rej-hint" className="mt-1 text-xs text-text-muted">{t('rejectHint')}</p>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setRejecting(false)} disabled={busy}>{t('keep')}</Button>
            <Button variant="destructive" onClick={() => void reject()} isLoading={busy} data-testid="reject-confirm">{t('rejectConfirm')}</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}

export function QuoteDetailView({ companyId, quoteId }: { companyId: string; quoteId: string }) {
  const t = useTranslations('BuyerQuotes');
  const toPath = useLocalePath();
  return (
    <PageShell title={t('detailTitle')} back={{ href: toPath(`/account/company/${encodeURIComponent(companyId)}/quotes`), label: t('backToList') }}>
      <RequireSignIn>
        <Panel companyId={companyId} quoteId={quoteId} />
      </RequireSignIn>
    </PageShell>
  );
}
