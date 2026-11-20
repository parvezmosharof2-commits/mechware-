'use client';

import Link from 'next/link';
import { dhakaWhen, flagWords, hasCostFigures, paymentTermsLabel, quoteMoney, shippingModeLabel, type StaffQuoteVersion } from '@mechware/api-client';
import { QuoteStatusBadge } from './QuoteStatusBadge';

const VERSION_EVENTS: readonly [keyof StaffQuoteVersion, string][] = [
  ['submittedAt', 'Submitted for approval'], ['approvedAt', 'Approved'], ['approvalRejectedAt', 'Approval refused'], ['sentAt', 'Sent to the buyer'],
  ['validUntil', 'Valid until'], ['acceptedAt', 'Accepted by the buyer'], ['rejectedAt', 'Rejected by the buyer'], ['withdrawnAt', 'Withdrawn'], ['supersededAt', 'Replaced by a newer version']
];

/** One version of a quote. `highlight` marks lines (by id) the reader has to look at (approval screen); the words under the part name say why, so a colour is never the only sign. */
export function VersionView({ v, quoteId, latest, canEdit, highlight }: { v: StaffQuoteVersion; quoteId: string; latest: boolean; canEdit: boolean; highlight?: ReadonlySet<string> }) {
  const showCost = hasCostFigures(v.lines);
  const lineNos = new Map(v.lines.map((l) => [l.id, l.lineNo] as const));
  const flags = flagWords(v.flags, lineNos);
  const headingId = `version-${v.versionNo}`;
  return (
    <section aria-labelledby={headingId} className="space-y-3 rounded-xl border border-surface-border bg-surface p-4" data-testid="quote-version" data-version-no={v.versionNo}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id={headingId} className="font-display text-base font-bold">Version {v.versionNo}{latest ? ' (latest)' : ''}</h2>
        <QuoteStatusBadge status={v.status} />
      </div>
      <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-3">
        <div><dt className="text-xs text-text-muted">Valid for</dt><dd>{v.validityDays} days</dd></div>
        <div><dt className="text-xs text-text-muted">Payment</dt><dd>{paymentTermsLabel(v.paymentTerms)}</dd></div>
        <div><dt className="text-xs text-text-muted">Delivery charge</dt><dd>{shippingModeLabel(v.shippingMode)}</dd></div>
        <div><dt className="text-xs text-text-muted">Prepared by</dt><dd>{v.preparerName ?? 'Not set yet'}</dd></div>
        <div><dt className="text-xs text-text-muted">Created</dt><dd className="font-mono text-xs">{dhakaWhen(v.createdAt)}</dd></div>
        {VERSION_EVENTS.filter(([k]) => v[k]).map(([k, label]) => <div key={k}><dt className="text-xs text-text-muted">{label}</dt><dd className="font-mono text-xs">{dhakaWhen(String(v[k]))}</dd></div>)}
      </dl>
      {v.withdrawReason && <p className="text-sm"><span className="text-xs text-text-muted">Withdrawn because: </span>{v.withdrawReason}</p>}
      {v.rejectReason && <p className="text-sm"><span className="text-xs text-text-muted">Rejected because: </span>{v.rejectReason}</p>}
      {v.approvalComment && <p className="text-sm"><span className="text-xs text-text-muted">Approval comment: </span>{v.approvalComment}</p>}
      {v.buyerNote && <p className="whitespace-pre-wrap break-words text-sm"><span className="text-xs text-text-muted">Note to the buyer: </span>{v.buyerNote}</p>}
      {v.termsText && <p className="whitespace-pre-wrap break-words text-sm"><span className="text-xs text-text-muted">Terms: </span>{v.termsText}</p>}
      {v.internalNote && <p className="whitespace-pre-wrap break-words text-sm"><span className="text-xs text-text-muted">Internal note (the buyer does not see it): </span>{v.internalNote}</p>}
      {flags.length > 0 && (
        <ul aria-label="Checks that need a decision" className="space-y-1 rounded-md border border-fault p-3 text-sm font-medium text-fault" data-testid="version-flags">
          {flags.map((f) => <li key={f}>{f}</li>)}
        </ul>
      )}

      {v.lines.length === 0 ? (
        <p className="text-sm text-text-muted" data-testid="version-no-lines">No line yet.</p>
      ) : (
        <div className="overflow-x-auto rounded-md border border-surface-border">
          <table className="w-full text-left text-sm" data-testid="version-lines">
            <caption className="sr-only">Lines of version {v.versionNo}</caption>
            <thead className="bg-bg-subtle text-xs text-text-muted">
              <tr>
                <th scope="col" className="px-3 py-2">No.</th>
                <th scope="col" className="px-3 py-2">Part</th>
                <th scope="col" className="px-3 py-2 text-right">Quantity</th>
                <th scope="col" className="px-3 py-2 text-right">Unit price (BDT)</th>
                <th scope="col" className="px-3 py-2 text-right">Net (BDT)</th>
                <th scope="col" className="px-3 py-2 text-right">VAT (BDT)</th>
                <th scope="col" className="px-3 py-2 text-right">Total (BDT)</th>
                {showCost && <th scope="col" className="px-3 py-2 text-right" data-testid="cost-head">Cost / floor (BDT)</th>}
              </tr>
            </thead>
            <tbody>
              {v.lines.map((l) => (
                <tr key={l.id} className={'border-t border-surface-border align-top' + (highlight?.has(l.id) ? ' bg-bg-subtle outline outline-2 -outline-offset-2 outline-fault' : '')} data-testid="version-line" data-flagged={highlight?.has(l.id) ? 'true' : undefined}>
                  <td className="px-3 py-2">{l.lineNo}</td>
                  <td className="px-3 py-2">
                    <span className="font-mono text-xs">{l.sku}</span> {l.titleEn}
                    {l.partNumber && <span className="block text-xs text-text-muted">Part no. {l.partNumber}</span>}
                    {l.lineNote && <span className="block text-xs text-text-muted">{l.lineNote}</span>}
                    {l.belowFloor && <span className="block text-xs font-bold text-fault">Below the margin floor{l.belowFloorReason ? `: ${l.belowFloorReason}` : ''}</span>}
                    {l.overAuthority && <span className="block text-xs font-bold text-fault">Beyond the preparer&apos;s price authority</span>}
                  </td>
                  <td className="px-3 py-2 text-right">{l.quantity}</td>
                  <td className="px-3 py-2 text-right font-mono">{quoteMoney(l.unitPriceMinor)}</td>
                  <td className="px-3 py-2 text-right font-mono">{quoteMoney(l.lineNetMinor)}</td>
                  <td className="px-3 py-2 text-right font-mono">{quoteMoney(l.lineTaxMinor)}</td>
                  <td className="px-3 py-2 text-right font-mono">{quoteMoney(l.lineTotalMinor)}</td>
                  {showCost && (
                    <td className="px-3 py-2 text-right font-mono" data-testid="cost-cell">
                      {l.cost ? `${quoteMoney(l.cost.costMinorAtCheck) || 'no cost'} / ${quoteMoney(l.cost.floorMinorAtCheck) || 'no floor'}` : ''}
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <dl className="ml-auto grid max-w-sm grid-cols-2 gap-x-4 gap-y-1 text-sm" data-testid="version-totals">
        <dt>Goods, net</dt><dd className="text-right font-mono">BDT {quoteMoney(v.goodsNetMinor)}</dd>
        <dt>VAT</dt><dd className="text-right font-mono">BDT {quoteMoney(v.taxMinor)}</dd>
        <dt>Goods, total</dt><dd className="text-right font-mono">BDT {quoteMoney(v.goodsTotalMinor)}</dd>
        <dt>Delivery</dt><dd className="text-right font-mono">{v.shippingTotalMinor === null ? 'Worked out when the buyer orders' : `BDT ${quoteMoney(v.shippingTotalMinor)}`}</dd>
        {v.grandTotalMinor !== null && <><dt className="font-semibold">Total</dt><dd className="text-right font-mono font-semibold">BDT {quoteMoney(v.grandTotalMinor)}</dd></>}
      </dl>
      {latest && v.status === 'draft' && (
        <p className="text-sm text-text-muted" data-testid="editor-placeholder">
          {canEdit
            ? <><Link href={`/quotes/${quoteId}/edit`} className="font-semibold text-brand-accent underline" data-testid="edit-draft">Edit this draft</Link>: lines, prices, terms, the price check and the send.</>
            : 'You can read this draft. Editing needs the quotes.edit permission.'}
        </p>
      )}
    </section>
  );
}
