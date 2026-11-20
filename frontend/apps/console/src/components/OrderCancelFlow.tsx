'use client';

import * as React from 'react';
import Link from 'next/link';
import { cancelOutcome, cancelReasonProblem, canViewRefunds, isStaffStepUpFailure, type CancelOutcome } from '@mechware/api-client';
import { staffOrderError, staffOrdersApi } from '../lib/api';
import { useFocusOn } from './CreditShared';

// The dialogs (and the library under them) load only when a cancel starts, so the pages keep a small first load.
const Dialogs = React.lazy(() => import('./OrderCancelDialogs'));

export interface CancelTarget { orderId: string; orderNumber: string }
type Flow = { k: 'reason'; order: CancelTarget } | { k: 'code'; order: CancelTarget; reason: string } | null;

/**
 * The staff cancel flow (E7.2) shared by the overdue list and the order detail: reason (5-500, checked BEFORE a code is asked for, because a code is spent
 * even when the call is refused), then the authenticator code. `banner` is the result notice or alert; `dialogs` goes anywhere on the page.
 * `onChanged` runs after a cancel and after a refusal that means the screen is out of date, so the caller reloads.
 */
export function useOrderCancel(permissions: readonly string[], onChanged: () => void) {
  const [flow, setFlow] = React.useState<Flow>(null);
  const [reason, setReason] = React.useState('');
  const [problem, setProblem] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [codeError, setCodeError] = React.useState<string | null>(null);
  const [done, setDone] = React.useState<CancelOutcome | null>(null);
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const alertRef = useFocusOn(alertText);
  const noticeRef = React.useRef<HTMLDivElement>(null);
  React.useEffect(() => { if (done) noticeRef.current?.focus(); }, [done]);

  const close = () => { setFlow(null); setCodeError(null); setProblem(null); setReason(''); };
  const open = (order: CancelTarget) => { setDone(null); setAlertText(null); setReason(''); setProblem(null); setFlow({ k: 'reason', order }); };

  const toCode = () => {
    if (flow?.k !== 'reason') return;
    const pr = cancelReasonProblem(reason);
    if (pr) return setProblem(pr);
    setProblem(null); setCodeError(null);
    setFlow({ k: 'code', order: flow.order, reason: reason.trim() });
  };

  const submit = async (code: string) => {
    if (flow?.k !== 'code') return;
    setBusy(true);
    setCodeError(null);
    try {
      const res = await staffOrdersApi.cancel(flow.order.orderId, flow.reason, code);
      close();
      setDone(cancelOutcome(res.data));
      onChanged();
    } catch (err) {
      const f = staffOrderError(err);
      if (isStaffStepUpFailure(f.code)) { setCodeError(f.text); return; }
      close();
      setAlertText(f.text);
      if (f.reload) onChanged();
    } finally { setBusy(false); }
  };

  const banner = (
    <>
      {done && (
        <div ref={noticeRef} tabIndex={-1} role="status" data-testid="cancel-notice" className="space-y-1 rounded-md border border-surface-border bg-bg-subtle p-3 text-sm font-medium">
          {done.lines.map((l) => <p key={l}>{l}</p>)}
          {done.warnings.map((w) => <p key={w} role="alert" data-testid="cancel-warning" className="text-fault">{w}</p>)}
          {done.needsRefund && canViewRefunds(permissions) && <p><Link href="/refunds" className="text-brand-accent underline">Open the refund queue</Link></p>}
        </div>
      )}
      {alertText && <div ref={alertRef} tabIndex={-1} role="alert" data-testid="cancel-alert" className="rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">{alertText}</div>}
    </>
  );

  const dialogs = flow ? (
    <React.Suspense fallback={null}>
      <Dialogs step={flow.k} orderNumber={flow.order.orderNumber} reason={flow.k === 'code' ? flow.reason : reason} onReason={setReason} problem={problem} busy={busy}
        codeError={codeError} onContinue={toCode} onClose={close} onSubmit={submit} />
    </React.Suspense>
  ) : null;

  return { open, busy, banner, dialogs };
}
