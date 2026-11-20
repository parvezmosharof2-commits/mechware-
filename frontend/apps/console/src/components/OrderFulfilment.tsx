'use client';

import * as React from 'react';
import { Button } from '@mechware/ui/src/components/Button';
import {
  FULFIL_MISSING_PERMISSION_TEXT, canFulfilOrders, dhakaWhen, nextFulfilStep, noStepReason, safeTrackingUrl, shipFieldErrors,
  type FulfilStep, type ShipFields, type ShipProblems, type StaffOrderDetail, type StaffShipment
} from '@mechware/api-client';
import { fulfilError, staffOrdersApi } from '../lib/api';
import { useFocusOn } from './CreditShared';

// The ship form (and the dialog library under it) loads only when a ship starts, so the page keeps a small first load.
const ShipDialog = React.lazy(() => import('./ShipDialog'));

/**
 * Manual fulfilment (F1.3): the one next step as a button (only with orders.fulfil, otherwise the reason), the ship form and the result.
 * Every move sends the rowVersion the screen holds; `onChanged` reloads the order after a move and after a refusal that means the screen is out of date.
 */
export function useOrderFulfil(permissions: readonly string[], d: StaffOrderDetail | null, onChanged: () => void) {
  const [shipOpen, setShipOpen] = React.useState(false);
  const [busy, setBusy] = React.useState(false);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const [movedFrom, setMovedFrom] = React.useState<number | null>(null);
  const alertRef = useFocusOn(alertText);
  const noticeRef = useFocusOn(notice);

  const action = d ? nextFulfilStep(d.status) : null;
  // After a move the old button stays hidden until the reload brings the new rowVersion, so a quick second click cannot send a stale one.
  const settling = !!d && movedFrom === d.rowVersion;

  const run = async (step: FulfilStep, ship?: ShipFields): Promise<ShipProblems | null> => {
    if (!d || !action) return null;
    setBusy(true); setNotice(null); setAlertText(null);
    try {
      await staffOrdersApi.fulfil(d.id, step, d.rowVersion, ship);
      setShipOpen(false);
      setMovedFrom(d.rowVersion);
      setNotice(`Order ${d.orderNumber} ${action.doneText}`);
      onChanged();
    } catch (err) {
      const f = fulfilError(step, err);
      if (step === 'ship' && f.status === 400) {
        const p = shipFieldErrors(f.fields);
        if (Object.keys(p).length) return p;
      }
      setShipOpen(false);
      setAlertText(f.text);
      if (f.reload) onChanged();
    } finally { setBusy(false); }
    return null;
  };

  const control = !d ? null : !canFulfilOrders(permissions)
    ? <p className="max-w-xs text-xs text-text-muted" data-testid="fulfil-hint">{FULFIL_MISSING_PERMISSION_TEXT}</p>
    : !action
      ? <p className="max-w-xs text-xs text-text-muted" data-testid="fulfil-hint">{noStepReason(d.status)}</p>
      : settling ? null : (
        <div className="space-y-1">
          <Button type="button" data-testid="fulfil-button" disabled={busy} onClick={() => (action.step === 'ship' ? setShipOpen(true) : void run(action.step))}>{action.label}</Button>
          <p className="max-w-xs text-xs text-text-muted">A step cannot be undone.</p>
        </div>
      );

  const banner = (
    <>
      {notice && <div ref={noticeRef} tabIndex={-1} role="status" data-testid="fulfil-notice" className="rounded-md border border-surface-border bg-bg-subtle p-3 text-sm font-medium">{notice}</div>}
      {alertText && <div ref={alertRef} tabIndex={-1} role="alert" data-testid="fulfil-alert" className="rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">{alertText}</div>}
    </>
  );

  const dialog = shipOpen && d ? (
    <React.Suspense fallback={null}>
      <ShipDialog orderNumber={d.orderNumber} busy={busy} onClose={() => setShipOpen(false)} onSubmit={(f) => run('ship', f)} />
    </React.Suspense>
  ) : null;

  return { control, banner, dialog };
}

/** What was handed to the courier and when; Dhaka time. */
export function ShipmentBlock({ shipments }: { shipments: StaffShipment[] }) {
  const id = React.useId();
  if (shipments.length === 0) return null;
  return (
    <section aria-labelledby={id} className="space-y-2" data-testid="shipment-block">
      <h2 id={id} className="text-base font-semibold">Shipment</h2>
      {shipments.map((s) => {
        const link = safeTrackingUrl(s.trackingUrl);
        const rows: Array<[string, React.ReactNode]> = [
          ['Courier', s.courierName], ['Tracking number', <span key="n" className="font-mono">{s.trackingNumber}</span>],
          ['Tracking link', link ? <a key="l" href={link} target="_blank" rel="noopener noreferrer" className="break-all text-brand-accent underline">{link}</a> : 'None given'],
          ['Shipped', dhakaWhen(s.shippedAtUtc)], ['Delivered', s.deliveredAtUtc ? dhakaWhen(s.deliveredAtUtc) : 'Not delivered yet'],
          ['Entered by', s.createdBy ? `staff ${s.createdBy.slice(0, 8)}` : 'the system']
        ];
        return (
          <dl key={s.id} className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2 lg:grid-cols-3" data-testid="shipment">
            {rows.map(([k, v]) => <div key={k} className="min-w-0"><dt className="text-xs text-text-muted">{k}</dt><dd className="break-words font-medium">{v}</dd></div>)}
          </dl>
        );
      })}
    </section>
  );
}
