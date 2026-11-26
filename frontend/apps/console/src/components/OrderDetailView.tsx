'use client';

import * as React from 'react';
import Link from 'next/link';
import { Badge } from '@mechware/ui/src/components/Badge';
import { Button } from '@mechware/ui/src/components/Button';
import {
  REFUND_STATUS_LABEL, cancelHint, canPrintChallan, canViewInvoices, canViewRefunds, canViewStaffOrders, creditStateText, dhakaWhen, formatBdt, orderStatusText, paymentStateText, paymentStatusText,
  priceSnapshotText, refundStatus, timelineText, type StaffOrderDetail, type StaffOrderPayment
} from '@mechware/api-client';
import { staffOrderError, staffOrdersApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { BackLink, FailureAlert, Loading, NoAccess } from './CreditShared';
import { useOrderCancel } from './OrderCancelFlow';
import { PrintChallan, PrintMushak } from './InvoicePdfButtons';
import { ShipmentBlock, useOrderFulfil } from './OrderFulfilment';

const th = 'px-3 py-2';
const money = (minor: number, currency = 'BDT') => `${currency} ${formatBdt(minor)}`;
const shortId = (id: string | null) => (id ? id.slice(0, 8) : 'the system');

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  const id = React.useId();
  return <section aria-labelledby={id} className="space-y-2"><h2 id={id} className="text-base font-semibold">{title}</h2>{children}</section>;
}

function Facts({ rows }: { rows: Array<[string, React.ReactNode]> }) {
  return (
    <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2 lg:grid-cols-3">
      {rows.filter(([, v]) => v !== null && v !== '').map(([k, v]) => <div key={k}><dt className="text-xs text-text-muted">{k}</dt><dd className="font-medium">{v}</dd></div>)}
    </dl>
  );
}

function PaymentBlock({ p, d, canRefunds }: { p: StaffOrderPayment; d: StaffOrderDetail; canRefunds: boolean }) {
  return (
    <li className="space-y-2 rounded-md border border-surface-border p-3 text-sm" data-testid="payment">
      <p className="flex flex-wrap items-center gap-2">
        <span className="font-semibold">{money(p.amountMinor, p.currency)}</span>
        <Badge variant="outline">{paymentStatusText(p.status)}</Badge>
        <span className="text-text-muted">by {p.method}, started {dhakaWhen(p.createdAtUtc)}{p.capturedAtUtc ? `, paid ${dhakaWhen(p.capturedAtUtc)}` : ''}</span>
      </p>
      {p.status === 'needs_refund' && canRefunds && <p><Link href="/refunds" className="text-brand-accent underline">Open the refund queue</Link></p>}
      {p.refunds.length > 0 && (
        <ul className="space-y-1 border-t border-surface-border pt-2" aria-label={`Refunds of the ${money(p.amountMinor, d.currency)} payment`}>
          {p.refunds.map((r) => (
            <li key={r.id} data-testid="refund">
              {money(r.amountMinor, p.currency)}: <span className="font-medium">{REFUND_STATUS_LABEL[refundStatus(r)]}</span>. Reason: {r.reason}.
              Asked {dhakaWhen(r.createdAtUtc)} by staff {shortId(r.requestedBy)}{r.decidedBy ? `, decided ${dhakaWhen(r.decidedAtUtc)} by staff ${shortId(r.decidedBy)}` : ''}.
            </li>
          ))}
        </ul>
      )}
    </li>
  );
}

function Detail({ id, permissions }: { id: string; permissions: readonly string[] }) {
  const res = useResource(() => staffOrdersApi.order(id), id);
  const d = res.data?.data ?? null;
  const failure = res.error ? staffOrderError(res.error) : null;
  const cancel = useOrderCancel(permissions, res.reload);
  const fulfil = useOrderFulfil(permissions, d, res.reload);
  const canRefunds = canViewRefunds(permissions);
  const hint = d ? cancelHint(d, permissions) : null;

  return (
    <div className="space-y-6">
      <BackLink href="/orders">Back to all orders</BackLink>
      {cancel.banner}
      {fulfil.banner}
      <FailureAlert failure={failure} onRetry={res.reload} />
      {res.loading && !d && <Loading />}
      {d && (
        <>
          <header className="flex flex-wrap items-start justify-between gap-3">
            <div className="space-y-1">
              <h2 className="font-mono text-xl font-bold" data-testid="order-number">{d.orderNumber}</h2>
              <p className="flex flex-wrap items-center gap-2 text-sm">
                <Badge variant="outline" data-testid="order-status">Status: {orderStatusText(d.status)}</Badge>
                <Badge variant="outline" data-testid="order-payment-state">Payment: {paymentStateText(d.paymentState)}</Badge>
                <Badge variant="outline">{d.source === 'quote' ? 'From a quote' : 'From a cart'}</Badge>
              </p>
            </div>
            <div className="flex flex-wrap items-start gap-3">
              {fulfil.control}
              {hint && permissions.some((p) => p.toLowerCase() === 'orders.cancel') && (
                hint.allowed
                  ? <Button type="button" variant="destructive" disabled={cancel.busy} onClick={() => cancel.open({ orderId: d.id, orderNumber: d.orderNumber })}>Cancel order</Button>
                  : <span className="max-w-xs text-xs text-text-muted" data-testid="cancel-hint">{hint.reason}</span>
              )}
            </div>
          </header>

          <Section title="Order">
            <Facts rows={[
              ['Company', d.companyName], ['Buyer', d.buyer.name], ['Buyer phone', d.buyer.phone], ['Purchase order number', d.purchaseOrderNumber],
              ['Placed', dhakaWhen(d.placedAtUtc)], ['Confirmed', dhakaWhen(d.confirmedAtUtc)], ['Payment due', dhakaWhen(d.paymentDueAtUtc)],
              ['Legal delivery deadline', dhakaWhen(d.legalDeliveryDeadlineUtc)], ['Courier handover deadline', dhakaWhen(d.courierHandoverDeadlineUtc)],
              ['Tax mode', d.taxMode.replace(/_/g, ' ')],
              ['Quote', d.quote ? <Link href={`/quotes/${d.quote.quoteId}`} className="text-brand-accent underline" data-testid="quote-link">Quote {d.quote.number}</Link> : null]
            ]} />
          </Section>

          <ShipmentBlock shipments={d.shipments ?? []} />
          {(d.shipments?.length ?? 0) > 0 && canPrintChallan(permissions) && (
            <Section title="Delivery challan"><PrintChallan orderId={d.id} orderNumber={d.orderNumber} /></Section>
          )}

          {(d.invoiceNumbers?.length ?? 0) > 0 && (
            <Section title="Invoice (Mushak-6.3)">
              <p className="text-sm" data-testid="invoice-numbers">{d.invoiceNumbers!.join(', ')}</p>
              {canViewInvoices(permissions) ? <PrintMushak orderId={d.id} orderNumber={d.orderNumber} /> : <p className="text-xs text-text-muted">Printing needs the invoices.view permission.</p>}
            </Section>
          )}

          <Section title="Delivery address">
            {d.shipTo ? (
              <address className="text-sm not-italic" data-testid="ship-to">
                {d.shipTo.recipientName}, {d.shipTo.phone}<br />
                {d.shipTo.siteName && <>{d.shipTo.siteName}<br /></>}
                {[d.shipTo.line1, d.shipTo.line2, d.shipTo.upazila, d.shipTo.district, d.shipTo.division, d.shipTo.postalCode].filter(Boolean).join(', ')}
              </address>
            ) : <p className="text-sm text-text-muted">No delivery address is recorded on this order.</p>}
          </Section>

          <Section title="Credit">
            {d.credit ? (
              <Facts rows={[['Credit state', creditStateText(d.credit.status)], ['Due on', d.credit.dueOn], ['Charged', money(d.credit.chargedMinor, d.currency)], ['Still owed', money(d.credit.outstandingMinor, d.currency)]]} />
            ) : <p className="text-sm text-text-muted" data-testid="no-credit">{d.paysOnCredit ? 'This order is on credit, but no charge is recorded (for example it was cancelled).' : 'This order is not on credit.'}</p>}
          </Section>

          <Section title="Lines">
            {d.subOrders.map((s) => (
              <div key={s.id} className="space-y-1" data-testid="sub-order">
                <p className="text-sm"><span className="font-semibold">{s.storeName}</span> <span className="font-mono text-xs">{s.subOrderNumber}</span>, {orderStatusText(s.status)}, shipping {money(s.shippingMinor, d.currency)}</p>
                <div className="overflow-x-auto rounded-md border border-surface-border" role="region" tabIndex={0} aria-label={`Lines of sub-order ${s.subOrderNumber}`}>
                  <table className="w-full text-left text-sm">
                    <caption className="sr-only">Lines of sub-order {s.subOrderNumber} from {s.storeName}, with the price at order time</caption>
                    <thead className="bg-bg-subtle text-xs text-text-muted">
                      <tr>
                        <th scope="col" className={th}>Item</th><th scope="col" className={`${th} text-right`}>Qty</th><th scope="col" className={`${th} text-right`}>Unit price</th>
                        <th scope="col" className={`${th} text-right`}>VAT %</th><th scope="col" className={`${th} text-right`}>Discount</th>
                        <th scope="col" className={`${th} text-right`}>Line total</th><th scope="col" className={th}>Price source</th>
                      </tr>
                    </thead>
                    <tbody>
                      {s.lines.map((l) => (
                        <tr key={l.variantId} className="border-t border-surface-border align-top" data-testid="order-line">
                          <td className="px-3 py-2">{l.title}<span className="block font-mono text-xs text-text-muted">{l.sku}</span></td>
                          <td className="px-3 py-2 text-right">{l.quantity}</td>
                          <td className="whitespace-nowrap px-3 py-2 text-right font-mono text-xs">{formatBdt(l.unitPriceMinor)}</td>
                          <td className="px-3 py-2 text-right">{l.vatRate}</td>
                          <td className="whitespace-nowrap px-3 py-2 text-right font-mono text-xs">{formatBdt(l.discountMinor)}</td>
                          <td className="whitespace-nowrap px-3 py-2 text-right font-mono text-xs">{formatBdt(l.totalMinor)}</td>
                          <td className="px-3 py-2 text-xs">{priceSnapshotText(l.priceSnapshot)}{l.priceSnapshot.publicUnitPriceMinor != null && <span className="block text-text-muted">Public price then: {formatBdt(l.priceSnapshot.publicUnitPriceMinor)}</span>}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </div>
            ))}
          </Section>

          <Section title="Totals">
            <Facts rows={[['Subtotal', money(d.subtotalMinor, d.currency)], ['Discount', money(d.discountMinor, d.currency)], ['Tax', money(d.taxMinor, d.currency)], ['Shipping', money(d.shippingMinor, d.currency)], ['Grand total', <strong key="g" data-testid="grand-total">{money(d.grandTotalMinor, d.currency)}</strong>]]} />
          </Section>

          <Section title="Payments and refunds">
            {d.payments.length === 0 ? <p className="text-sm text-text-muted" data-testid="no-payments">No payment was made on this order.</p> : (
              <ul className="space-y-2">{d.payments.map((p) => <PaymentBlock key={p.id} p={p} d={d} canRefunds={canRefunds} />)}</ul>
            )}
          </Section>

          <Section title="Timeline">
            <ol className="space-y-1 text-sm" data-testid="timeline">
              {d.events.map((e) => (
                <li key={e.sequence} data-testid="timeline-event"><span className="font-mono text-xs">{dhakaWhen(e.atUtc)}</span> {timelineText(e)}{e.reason ? `. Reason: ${e.reason}` : ''} <span className="text-text-muted">(by {e.actorId ? `staff or buyer ${shortId(e.actorId)}` : 'the system'})</span></li>
              ))}
            </ol>
          </Section>
        </>
      )}
      {cancel.dialogs}
      {fulfil.dialog}
    </div>
  );
}

export function OrderDetailView({ id }: { id: string }) {
  const { user } = useAuth();
  const permissions = user?.permissions ?? [];
  return (
    <ConsoleShell activeItemId="orders-all" title="Order">
      {canViewStaffOrders(permissions) ? <Detail id={id} permissions={permissions} /> : <NoAccess permission="orders.view_all" />}
    </ConsoleShell>
  );
}
