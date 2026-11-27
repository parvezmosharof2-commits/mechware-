'use client';

import * as React from 'react';
import Link from 'next/link';
import { Badge } from '@mechware/ui/src/components/Badge';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import {
  MANAGE_MISSING_TEXT, MONEY_BACK_REJECTED_TEXT, STAFF_REASON_LABEL, canManageReturns, canRejectCreditReduction, isCreditNoteStepUp, creditNoteErrorText, canViewStaffOrders, dhakaWhen, inspectBody, inspectionProblems, noReturnStepText, returnSteps, staffReturnStatusText,
  type InspectionRow, type ReturnPhoto, type StaffReturn
} from '@mechware/api-client';
import { staffReturnError, staffReturnsApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { BackLink, FailureAlert, FocusAlert, Loading, NoAccess, Notice, useFocusOn } from './CreditShared';
import { Modal } from './Modal';
import { ReasonDialog } from './ReasonDialog';
import { StepUpDialog } from './StepUpDialog';

const shortId = (id: string | null) => (id ? id.slice(0, 8) : 'the system');

function Facts({ rows }: { rows: Array<[string, React.ReactNode]> }) {
  return (
    <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2 lg:grid-cols-3">
      {rows.filter(([, v]) => v !== null && v !== '').map(([k, v]) => <div key={k} className="min-w-0"><dt className="text-xs text-text-muted">{k}</dt><dd className="break-words font-medium">{v}</dd></div>)}
    </dl>
  );
}

/** Per line: accepted (at most what was asked), restocked (at most accepted; the rest is scrapped) and an internal note. Checked here before anything is sent. */
function InspectForm({ d, busy, onSubmit }: { d: StaffReturn; busy: boolean; onSubmit: (rows: InspectionRow[]) => void }) {
  const [rows, setRows] = React.useState<InspectionRow[]>(() => d.lines.map((l) => ({ returnLineId: l.id, asked: l.quantity, accepted: String(l.quantity), restock: String(l.quantity), note: '' })));
  const [problems, setProblems] = React.useState<ReturnType<typeof inspectionProblems>>({});
  const set = (i: number, patch: Partial<InspectionRow>) => setRows((rs) => rs.map((r, j) => (j === i ? { ...r, ...patch } : r)));
  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    const found = inspectionProblems(rows);
    setProblems(found);
    if (Object.keys(found).length === 0) onSubmit(rows);
  };
  return (
    <form onSubmit={submit} noValidate className="space-y-4" data-testid="inspect-form" aria-label="Inspect the returned goods">
      <p className="text-sm text-text-muted">Count what arrived. Accepted units cannot exceed what was asked; restocked units cannot exceed accepted ones. Accepted but not restocked units count as scrapped. Restocked units go back to the warehouses the order took them from. This cannot be undone.</p>
      {d.lines.map((l, i) => {
        const p = problems[l.id] ?? {};
        return (
          <fieldset key={l.id} className="space-y-3 rounded-md border border-surface-border p-3" data-testid="inspect-line">
            <legend className="px-1 text-sm font-semibold">{l.title} <span className="font-mono text-xs font-normal text-text-muted">{l.sku}</span>, asked {l.quantity}</legend>
            <div className="grid gap-3 sm:grid-cols-3">
              <Input label="Accepted" type="number" inputMode="numeric" min={0} max={l.quantity} value={rows[i].accepted} error={p.accepted} data-testid={`accepted-${l.sku}`} onChange={(e) => set(i, { accepted: e.target.value })} />
              <Input label="Restocked" type="number" inputMode="numeric" min={0} value={rows[i].restock} error={p.restock} data-testid={`restock-${l.sku}`} onChange={(e) => set(i, { restock: e.target.value })} />
              <Input label="Internal note (optional)" value={rows[i].note} maxLength={500} error={p.note} data-testid={`note-${l.sku}`} onChange={(e) => set(i, { note: e.target.value })} />
            </div>
          </fieldset>
        );
      })}
      <Button type="submit" isLoading={busy} disabled={busy} data-testid="inspect-submit">Save the inspection</Button>
    </form>
  );
}

/* eslint-disable @next/next/no-img-element -- blob URLs from an authenticated fetch cannot go through next/image */
/** Read-only gallery of the buyer's photos; bytes come through the access-checked staff route, a click enlarges one in the lazy modal. */
function PhotoGallery({ returnId, orderNumber }: { returnId: string; orderNumber: string }) {
  const [photos, setPhotos] = React.useState<ReturnPhoto[]>([]);
  const [urls, setUrls] = React.useState<Record<string, string>>({});
  const [failed, setFailed] = React.useState(false);
  const [open, setOpen] = React.useState<number | null>(null);
  React.useEffect(() => {
    let dead = false;
    const made: string[] = [];
    staffReturnsApi.photos(returnId).then(async (list) => {
      if (dead) return;
      setPhotos(list);
      for (const p of list) {
        const b = await staffReturnsApi.photoBytes(returnId, p.id).catch(() => null);
        if (dead || !b) continue;
        const u = URL.createObjectURL(new Blob([b], { type: 'image/jpeg' }));
        made.push(u);
        setUrls((m) => ({ ...m, [p.id]: u }));
      }
    }).catch(() => { if (!dead) setFailed(true); });
    return () => { dead = true; made.forEach((u) => URL.revokeObjectURL(u)); };
  }, [returnId]);
  const alt = (i: number) => `Photo ${i + 1} of return ${orderNumber}`;
  const current = open === null ? null : photos[open];
  return (
    <section aria-labelledby="ret-photos" className="space-y-2" data-testid="return-photos">
      <h2 id="ret-photos" className="text-base font-semibold">Photos from the buyer</h2>
      {failed && <p role="alert" className="text-sm font-medium">The photos could not be loaded. Reload the page to try again.</p>}
      {!failed && photos.length === 0 && <p className="text-sm text-text-muted">The buyer added no photos.</p>}
      <ul className="flex flex-wrap gap-3">
        {photos.map((p, i) => (
          <li key={p.id}>
            <button type="button" onClick={() => setOpen(i)} aria-label={`Enlarge photo ${i + 1}`} data-testid="photo-open" className="block rounded-md focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand-accent">
              {urls[p.id] ? <img src={urls[p.id]} alt={alt(i)} className="h-28 w-28 rounded-md border border-surface-border object-cover" /> : <span className="block h-28 w-28 animate-pulse rounded-md bg-bg-subtle" aria-hidden="true" />}
            </button>
          </li>
        ))}
      </ul>
      <Modal open={current !== undefined && current !== null} onOpenChange={(o) => { if (!o) setOpen(null); }} title={open === null ? '' : alt(open)} description={current ? `${current.width} x ${current.height} px` : ''} contentClassName="max-w-3xl">
        {current && urls[current.id] && <img src={urls[current.id]} alt={alt(open ?? 0)} className="max-h-[70vh] w-full rounded-md object-contain" data-testid="photo-large" />}
      </Modal>
    </section>
  );
}

function Detail({ id, permissions, meId }: { id: string; permissions: readonly string[]; meId: string }) {
  const res = useResource(() => staffReturnsApi.get(id), id);
  const [fresh, setFresh] = React.useState<StaffReturn | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [rejectOpen, setRejectOpen] = React.useState(false);
  const [mbAsking, setMbAsking] = React.useState(false);
  const [mbReason, setMbReason] = React.useState<string | null>(null);
  const [mbError, setMbError] = React.useState<string | null>(null);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const alertRef = useFocusOn(alertText);
  const noticeRef = useFocusOn(notice);
  const failure = res.error ? staffReturnError(res.error) : null;
  const d = fresh ?? res.data?.data ?? null;
  const manage = canManageReturns(permissions);
  const mb = d ? canRejectCreditReduction(d, { id: meId, permissions }) : null;
  const steps = d ? returnSteps(d.status) : [];

  // The row version is the one on screen; a move answers with the new return, so a second click can never send a stale one.
  const run = async (what: string, call: (rowVersion: number) => ReturnType<typeof staffReturnsApi.approve>): Promise<boolean> => {
    if (!d) return false;
    setBusy(true); setNotice(null); setAlertText(null);
    try {
      setFresh((await call(d.rowVersion)).data);
      setNotice(`Return of order ${d.orderNumber}: ${what}.`);
      return true;
    } catch (err) {
      const f = staffReturnError(err);
      setAlertText(f.text);
      if (f.reload) { setFresh(null); res.reload(); }
      return false;
    } finally { setBusy(false); }
  };

  const rejectMoneyBack = async (code: string) => {
    if (!d || mbReason === null) return;
    setBusy(true); setMbError(null); setNotice(null); setAlertText(null);
    try {
      setFresh((await staffReturnsApi.rejectMoneyBack(d.id, mbReason, code)).data);
      setNotice(MONEY_BACK_REJECTED_TEXT); setMbReason(null);
    } catch (err) {
      const f = staffReturnError(err);
      if (isCreditNoteStepUp(f.code)) setMbError(creditNoteErrorText({ status: f.status, code: f.code }));
      else { setMbReason(null); setAlertText(f.text); if (f.reload) { setFresh(null); res.reload(); } }
    } finally { setBusy(false); }
  };

  return (
    <div className="space-y-6">
      <BackLink href="/returns">Back to all returns</BackLink>
      {notice && <div ref={noticeRef} tabIndex={-1}><Notice testId="return-notice">{notice}</Notice></div>}
      {alertText && <FocusAlert ref={alertRef} testId="return-alert">{alertText}</FocusAlert>}
      <FailureAlert failure={failure} onRetry={res.reload} />
      {res.loading && !d && <Loading />}
      {d && (
        <>
          <header className="flex flex-wrap items-start justify-between gap-3">
            <div className="space-y-1">
              <h2 className="font-mono text-xl font-bold" data-testid="return-order">{d.orderNumber}</h2>
              <p className="flex flex-wrap items-center gap-2 text-sm">
                <Badge variant="outline" data-testid="return-status">Status: {staffReturnStatusText(d.status)}</Badge>
                <Link href={`/orders/${d.orderId}`} className="font-semibold text-brand-accent underline" data-testid="order-link">Open the order</Link>
              </p>
            </div>
            <div className="flex flex-wrap items-start gap-3" data-testid="return-actions">
              {!manage ? <p className="max-w-xs text-xs text-text-muted" data-testid="manage-hint">{MANAGE_MISSING_TEXT}</p>
                : steps.length === 0 ? <p className="max-w-xs text-xs text-text-muted" data-testid="step-hint">{noReturnStepText(d.status)}</p>
                : (
                  <>
                    {steps.includes('approve') && <Button type="button" disabled={busy} data-testid="approve-button" onClick={() => void run('approved', (v) => staffReturnsApi.approve(d.id, v))}>Approve</Button>}
                    {steps.includes('reject') && <Button type="button" variant="destructive" disabled={busy} data-testid="reject-button" onClick={() => setRejectOpen(true)}>Reject</Button>}
                    {steps.includes('received') && <Button type="button" disabled={busy} data-testid="received-button" onClick={() => void run('goods marked as received', (v) => staffReturnsApi.received(d.id, v))}>Mark goods received</Button>}
                  </>
                )}
            </div>
          </header>

          {d.restockPending && (
            <div role="alert" data-testid="restock-pending" className="rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium">
              Some accepted units are not back on the shelf yet. The inspection is saved; the stock step failed or has not run. The nightly stock sweep finishes it by itself. If this stays after a day, tell stock operations.
            </div>
          )}
          {d.status === 'rejected' && d.rejectionReason && <p className="rounded-md border border-surface-border bg-bg-subtle p-3 text-sm" data-testid="rejection-reason"><span className="font-semibold">Rejection reason (the buyer sees it):</span> {d.rejectionReason}</p>}

          {mb?.show && (
            <section aria-labelledby="ret-mb" className="space-y-2 rounded-md border border-surface-border p-3" data-testid="money-back-pending">
              <h2 id="ret-mb" className="text-base font-semibold">Credit reduction waiting for a decision</h2>
              <p className="text-sm text-text-muted">A reduction of what the company owes on credit was asked. A second person approves it or rejects it. Rejecting puts the return back to Inspected so it can be asked again.</p>
              <Button type="button" variant="destructive" disabled={busy || !mb.canReject} aria-describedby={mb.hint ? 'mb-hint' : undefined} data-testid="money-back-reject" onClick={() => setMbAsking(true)}>Reject the credit reduction</Button>
              {mb.hint && <p id="mb-hint" className="text-sm font-medium text-text-muted" data-testid="money-back-hint">{mb.hint}</p>}
            </section>
          )}

          <section aria-labelledby="ret-facts" className="space-y-2">
            <h2 id="ret-facts" className="text-base font-semibold">Request</h2>
            <Facts rows={[
              ['Reason', STAFF_REASON_LABEL[d.reasonCode] ?? d.reasonCode], ['Buyer note', d.note], ['Company', d.companyId ? `company ${shortId(d.companyId)}` : 'Personal order'],
              ['Requested', dhakaWhen(d.requestedAtUtc)], ['Decided', d.decidedAtUtc ? `${dhakaWhen(d.decidedAtUtc)} by staff ${shortId(d.decidedBy)}` : ''],
              ['Goods received', d.receivedAtUtc ? `${dhakaWhen(d.receivedAtUtc)} by staff ${shortId(d.receivedBy)}` : ''],
              ['Inspected', d.inspectedAtUtc ? `${dhakaWhen(d.inspectedAtUtc)} by staff ${shortId(d.inspectedBy)}` : ''], ['Withdrawn', dhakaWhen(d.withdrawnAtUtc)]
            ]} />
          </section>

          <section aria-labelledby="ret-lines" className="space-y-2">
            <h2 id="ret-lines" className="text-base font-semibold">Lines</h2>
            <div className="overflow-x-auto rounded-md border border-surface-border" role="region" tabIndex={0} aria-label="Returned lines">
              <table className="w-full text-left text-sm">
                <caption className="sr-only">Returned lines with what was asked, accepted and restocked</caption>
                <thead className="bg-bg-subtle text-xs text-text-muted">
                  <tr><th scope="col" className="px-3 py-2">Item</th><th scope="col" className="px-3 py-2 text-right">Asked</th><th scope="col" className="px-3 py-2 text-right">Accepted</th><th scope="col" className="px-3 py-2 text-right">Restocked</th><th scope="col" className="px-3 py-2">Internal note</th></tr>
                </thead>
                <tbody>
                  {d.lines.map((l) => (
                    <tr key={l.id} className="border-t border-surface-border align-top" data-testid="return-line">
                      <td className="px-3 py-2">{l.title}<span className="block font-mono text-xs text-text-muted">{l.sku}</span></td>
                      <td className="px-3 py-2 text-right">{l.quantity}</td>
                      <td className="px-3 py-2 text-right" data-testid="line-accepted">{l.acceptedQuantity ?? 'Not inspected'}</td>
                      <td className="px-3 py-2 text-right" data-testid="line-restocked">{l.restockQuantity ?? 'Not inspected'}</td>
                      <td className="px-3 py-2">{l.inspectionNote}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>

          <PhotoGallery returnId={d.id} orderNumber={d.orderNumber} />

          {manage && steps.includes('inspect') && (
            <section aria-labelledby="ret-inspect" className="space-y-2">
              <h2 id="ret-inspect" className="text-base font-semibold">Inspection</h2>
              <InspectForm d={d} busy={busy} onSubmit={(rows) => void run('inspection saved', (v) => staffReturnsApi.inspect(d.id, inspectBody(v, rows)))} />
            </section>
          )}
          {mbAsking && <React.Suspense fallback={null}><ReasonDialog open title="Reject this credit reduction?" label="Reason (5 to 500 characters)" confirmLabel="Continue" busy={false}
            description="Nothing is reduced. The return goes back to Inspected and can be asked again. You will be asked for your authenticator code next."
            onCancel={() => setMbAsking(false)} onSubmit={(text) => { setMbAsking(false); setMbError(null); setMbReason(text); }} /></React.Suspense>}
          {mbReason !== null && <React.Suspense fallback={null}><StepUpDialog open title="Confirm the rejection" description={`This rejects the credit reduction for order ${d.orderNumber}.`} confirmLabel="Reject" busy={busy} error={mbError}
            onCancel={() => { setMbReason(null); setMbError(null); }} onSubmit={rejectMoneyBack} /></React.Suspense>}
          <ReasonDialog
            open={rejectOpen} title="Reject this return" description="The buyer sees this reason. Their units become returnable again." label="Reason (5 to 500 characters)"
            confirmLabel="Reject the return" busy={busy} onCancel={() => setRejectOpen(false)}
            onSubmit={(reason) => void run('rejected', (v) => staffReturnsApi.reject(d.id, v, reason)).then(() => setRejectOpen(false))}
          />
        </>
      )}
    </div>
  );
}

export function ReturnDetailView({ id }: { id: string }) {
  const { user } = useAuth();
  const permissions = user?.permissions ?? [];
  return (
    <ConsoleShell activeItemId="returns" title="Return">
      {canViewStaffOrders(permissions) ? <Detail id={id} permissions={permissions} meId={user?.id ?? ''} /> : <NoAccess permission="orders.view_all" />}
    </ConsoleShell>
  );
}
