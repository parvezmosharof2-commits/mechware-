'use client';

import * as React from 'react';
import { Button, Textarea } from '@mechware/ui';
import {
  PRICE_LIST_PERMISSIONS, actorName, belowFloorSummary, canApprove, canReject, canWithdraw, decisionReasonProblem, preparers, shortHash,
  type PriceListDetail
} from '@mechware/api-client';
import { decisionError, priceListsApi } from '../lib/api';
import { StepUpDialog } from './StepUpDialog';
import { Modal, ModalFooter } from './Modal';

const when = (iso: string | null | undefined) => (iso ? `${iso.replace('T', ' ').slice(0, 16)} UTC` : '');
const day = (iso: string) => iso.slice(0, 10);

/** What the approver saw when the dialog opened: this, and nothing newer, is what the approval is bound to. */
interface Snapshot {
  version: number;
  hash: string;
  validFromUtc: string;
  validToUtc: string;
  target: string;
  belowFloorIds: string[];
}

type Flow =
  | { kind: 'approve-confirm'; snap: Snapshot }
  | { kind: 'approve-code'; snap: Snapshot }
  | { kind: 'reject' }
  | { kind: 'withdraw-reason' }
  | { kind: 'withdraw-code'; reason: string }
  | null;

export const APPROVALS_CHANGED_EVENT = 'mw:approvals-changed';

/**
 * The decision part of the price-list screen: who prepared the list, the content hash, the below-floor entries, and the
 * approve / reject / withdraw buttons with their confirmation and step-up dialogs. Everything here is a hint: the server
 * checks permission, four eyes, the hash, the floors and the code again. No price is ever computed here.
 */
export function PriceListDecisionPanel(props: {
  detail: PriceListDetail;
  me: { id: string; permissions: readonly string[] };
  onDecided: (detail: PriceListDetail, message: string) => void;
  onReload: () => void;
}) {
  const { detail, me } = props;
  const [flow, setFlow] = React.useState<Flow>(null);
  const [busy, setBusy] = React.useState(false);
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const [codeError, setCodeError] = React.useState<string | null>(null);
  const [reason, setReason] = React.useState('');
  const [reasonError, setReasonError] = React.useState<string | null>(null);
  const [acknowledged, setAcknowledged] = React.useState(false);

  const held = new Set(me.permissions.map((p) => p.toLowerCase()));
  const viewCost = held.has(PRICE_LIST_PERMISSIONS.viewCost);
  const floor = belowFloorSummary(detail);
  const hint = canApprove(detail, me);
  const names = (ids: string[]) => ids.map((id) => actorName(detail, id)).join(', ');
  const target = detail.scope === 'Company' ? `the contract price for ${detail.companyName ?? 'the company'}${detail.contractRef ? ` (${detail.contractRef})` : ''}` : 'quantity-break prices for all customers';

  const done = (d: PriceListDetail, message: string) => {
    setFlow(null);
    setReason('');
    setAlertText(null);
    props.onDecided(d, message);
    window.dispatchEvent(new Event(APPROVALS_CHANGED_EVENT));
  };

  /** A refusal: the code dialog stays for a wrong code; anything else closes it, and stale data is reloaded. */
  const failed = (err: unknown, keepCodeOpen: boolean) => {
    const f = decisionError(err);
    if (keepCodeOpen && (f.code === 'STEP_UP_FAILED' || f.code === 'STEP_UP_REQUIRED')) return setCodeError(f.text);
    setCodeError(null);
    setFlow(null);
    setAlertText(f.text);
    if (f.reload) { props.onReload(); window.dispatchEvent(new Event(APPROVALS_CHANGED_EVENT)); }
  };

  const openApprove = () => {
    if (!detail.contentHash || !hint.allowed) return;
    setAlertText(null);
    setAcknowledged(false);
    setFlow({ kind: 'approve-confirm', snap: { version: detail.version, hash: detail.contentHash, validFromUtc: detail.validFromUtc, validToUtc: detail.validToUtc, target, belowFloorIds: floor.ids } });
  };

  const approve = async (snap: Snapshot, code: string) => {
    setBusy(true);
    setCodeError(null);
    try {
      const d = (await priceListsApi.approve(detail.id, { expectedContentHash: snap.hash, acknowledgedBelowFloorEntryIds: snap.belowFloorIds }, code)).data;
      done(d, `Approved version ${snap.version}. It starts to apply at ${when(d.effectiveFromUtc)}.`);
    } catch (err) {
      failed(err, true);
    } finally {
      setBusy(false);
    }
  };

  const reject = async () => {
    const problem = decisionReasonProblem(reason);
    if (problem) return setReasonError(problem);
    setReasonError(null);
    setBusy(true);
    try {
      done((await priceListsApi.reject(detail.id, reason)).data, 'Sent back to the author as a draft.');
    } catch (err) {
      failed(err, false);
    } finally {
      setBusy(false);
    }
  };

  const toWithdrawCode = () => {
    // The reason is checked here, BEFORE a code is asked for: a code is spent even when the action is then refused.
    const problem = decisionReasonProblem(reason);
    if (problem) return setReasonError(problem);
    setReasonError(null);
    setCodeError(null);
    setFlow({ kind: 'withdraw-code', reason: reason.trim() });
  };

  const withdraw = async (why: string, code: string) => {
    setBusy(true);
    setCodeError(null);
    try {
      done((await priceListsApi.withdraw(detail.id, why, code)).data, 'Withdrawn. New orders no longer get this price.');
    } catch (err) {
      failed(err, true);
    } finally {
      setBusy(false);
    }
  };

  const pending = detail.status === 'PendingApproval';
  const history: Array<[string, string]> = [];
  if (detail.submittedBy) history.push(['Submitted', `${actorName(detail, detail.submittedBy)}, ${when(detail.submittedAtUtc)}`]);
  if (detail.approvedBy) history.push(['Approved', `${actorName(detail, detail.approvedBy)}, ${when(detail.approvedAtUtc)}`]);
  if (detail.withdrawnBy) history.push(['Withdrawn', `${actorName(detail, detail.withdrawnBy)}, ${when(detail.withdrawnAtUtc)}${detail.withdrawReason ? `: ${detail.withdrawReason}` : ''}`]);
  if (detail.rejectedBy && detail.status !== 'Draft') history.push(['Earlier version sent back', `${actorName(detail, detail.rejectedBy)}, ${when(detail.rejectedAtUtc)}${detail.rejectionReason ? `: ${detail.rejectionReason}` : ''}`]);

  return (
    <section aria-label="Approval" className="space-y-4 rounded-xl border border-surface-border bg-surface p-6" data-testid="decision-panel">
      <h2 className="font-display text-lg font-bold">{pending ? 'Decision' : 'Approval history'}</h2>

      <dl className="grid grid-cols-1 gap-x-6 gap-y-1 text-sm sm:grid-cols-2">
        <div><dt className="inline text-text-muted">Created by: </dt><dd className="inline" data-testid="created-by">{actorName(detail, detail.createdBy)}</dd></div>
        {detail.submittedBy && <div><dt className="inline text-text-muted">Submitted by: </dt><dd className="inline" data-testid="submitted-by">{actorName(detail, detail.submittedBy)}{detail.submittedAtUtc ? `, ${when(detail.submittedAtUtc)}` : ''}</dd></div>}
        <div className="sm:col-span-2"><dt className="inline text-text-muted">Everyone who edited it (cannot approve): </dt><dd className="inline" data-testid="preparers">{names(preparers(detail))}</dd></div>
        <div><dt className="inline text-text-muted">Valid (UTC): </dt><dd className="inline font-mono">{day(detail.validFromUtc)} to {day(detail.validToUtc)}</dd></div>
        {detail.effectiveFromUtc && <div><dt className="inline text-text-muted">Applies from: </dt><dd className="inline font-mono">{when(detail.effectiveFromUtc)}</dd></div>}
      </dl>

      {detail.contentHash && (
        <div className="text-xs text-text-muted">
          Content hash: <span className="font-mono text-sm text-text" data-testid="hash-short">{shortHash(detail.contentHash, 12)}</span>
          <details className="mt-1"><summary className="cursor-pointer font-semibold text-brand-accent">Show the full hash</summary><span className="select-all break-all font-mono" data-testid="hash-full">{detail.contentHash}</span></details>
        </div>
      )}

      {pending && floor.count > 0 && (
        <div role="note" data-testid="below-floor-note" className="rounded-md border border-surface-border bg-bg-subtle p-3 text-sm">
          <p className="font-semibold">{floor.text}</p>
          <p className="text-xs text-text-muted">
            {viewCost ? 'They are marked in the table below with their cost and floor.' : 'They are highlighted in the table below. Amounts are shown only to roles with the cost permission.'}
          </p>
          <ul className="mt-1 list-disc pl-5 text-xs" data-testid="below-floor-list">
            {floor.entries.map((e) => <li key={e.id}><span className="font-mono">{e.sku}</span> (from {e.minQty}): {e.reason || 'no reason given'}</li>)}
          </ul>
        </div>
      )}

      {history.length > 0 && !pending && (
        <dl className="space-y-1 text-sm" data-testid="history">
          {history.map(([k, v]) => <div key={k}><dt className="inline text-text-muted">{k}: </dt><dd className="inline">{v}</dd></div>)}
        </dl>
      )}

      {alertText && <div role="alert" data-testid="decision-alert" className="rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">{alertText}</div>}

      {pending && canReject(detail, me) && (
        <div className="space-y-2">
          <div className="flex flex-wrap gap-3">
            <Button type="button" onClick={openApprove} disabled={busy || !hint.allowed} aria-describedby={hint.allowed ? undefined : 'approve-hint'}>Approve</Button>
            <Button type="button" variant="outline" disabled={busy} onClick={() => { setReasonError(null); setAlertText(null); setFlow({ kind: 'reject' }); }}>Reject</Button>
          </div>
          {!hint.allowed && <p id="approve-hint" data-testid="approve-hint" className="text-sm font-medium text-text-muted">{hint.reason}</p>}
        </div>
      )}
      {pending && !canReject(detail, me) && <p className="text-sm text-text-muted">Your role may not decide on price lists.</p>}

      {canWithdraw(detail, me) && (
        <div className="space-y-1">
          <Button type="button" variant="destructive" disabled={busy} onClick={() => { setReasonError(null); setAlertText(null); setFlow({ kind: 'withdraw-reason' }); }}>Withdraw</Button>
          <p className="text-xs text-text-muted">New orders will no longer get this price; placed orders keep theirs.</p>
        </div>
      )}

      <Modal open={flow?.kind === 'approve-confirm'} onOpenChange={(next) => { if (!next && !busy) setFlow(null); }} title="Approve this price list?" description={<>
              {flow?.kind === 'approve-confirm' && `You are approving version ${flow.snap.version}, hash ${shortHash(flow.snap.hash, 12)}, valid from ${day(flow.snap.validFromUtc)} to ${day(flow.snap.validToUtc)}, ${flow.snap.target}.`}
            </>} descriptionTestId="approve-statement">
          {flow?.kind === 'approve-confirm' && flow.snap.belowFloorIds.length > 0 && (
            <div className="space-y-2 rounded-md border border-surface-border bg-bg-subtle p-3 text-sm" data-testid="ack-box">
              <p className="font-semibold">{flow.snap.belowFloorIds.length === 1 ? '1 entry is' : `${flow.snap.belowFloorIds.length} entries are`} below the margin floor</p>
              <ul className="list-disc pl-5 text-xs">
                {floor.entries.filter((e) => flow.snap.belowFloorIds.includes(e.id)).map((e) => <li key={e.id}><span className="font-mono">{e.sku}</span> (from {e.minQty}): {e.reason || 'no reason given'}</li>)}
              </ul>
              {held.has(PRICE_LIST_PERMISSIONS.approveBelowFloor) ? (
                <label className="flex items-start gap-2 font-medium">
                  <input type="checkbox" className="mt-1" checked={acknowledged} onChange={(e) => setAcknowledged(e.target.checked)} />
                  <span>I acknowledge these {flow.snap.belowFloorIds.length} below-floor prices</span>
                </label>
              ) : (
                <p className="font-medium text-fault">An administrator with below-floor permission must approve this list.</p>
              )}
            </div>
          )}
          <ModalFooter>
            <Button type="button" variant="outline" onClick={() => setFlow(null)}>Cancel</Button>
            <Button
              type="button"
              data-testid="approve-continue"
              disabled={flow?.kind !== 'approve-confirm' || (flow.snap.belowFloorIds.length > 0 && (!acknowledged || !held.has(PRICE_LIST_PERMISSIONS.approveBelowFloor)))}
              onClick={() => { if (flow?.kind === 'approve-confirm') { setCodeError(null); setFlow({ kind: 'approve-code', snap: flow.snap }); } }}
            >
              Continue to the authenticator code
            </Button>
          </ModalFooter>
      </Modal>

      <StepUpDialog
        open={flow?.kind === 'approve-code'}
        title="Confirm the approval"
        description={flow?.kind === 'approve-code' ? `Approving version ${flow.snap.version} (hash ${shortHash(flow.snap.hash, 12)}).` : ''}
        confirmLabel="Approve"
        busy={busy}
        error={codeError}
        onCancel={() => { setFlow(null); setCodeError(null); }}
        onSubmit={async (code) => { if (flow?.kind === 'approve-code') await approve(flow.snap, code); }}
      />

      <Modal open={flow?.kind === 'reject'} onOpenChange={(next) => { if (!next && !busy) setFlow(null); }} title="Send this list back?" description="The list returns to draft so the author can correct it. No price changes. No authenticator code is needed.">
          <Textarea label="Reason (5 to 500 characters)" value={reason} onChange={(e) => setReason(e.target.value)} error={reasonError ?? undefined} maxLength={500} rows={3} />
          <ModalFooter>
            <Button type="button" variant="outline" onClick={() => setFlow(null)} disabled={busy}>Cancel</Button>
            <Button type="button" onClick={reject} isLoading={busy} disabled={busy}>Send back</Button>
          </ModalFooter>
      </Modal>

      <Modal open={flow?.kind === 'withdraw-reason'} onOpenChange={(next) => { if (!next && !busy) setFlow(null); }} title="Withdraw this price list?" description="New orders will no longer get this price; placed orders keep theirs. You will be asked for your authenticator code next.">
          <Textarea label="Reason (5 to 500 characters)" value={reason} onChange={(e) => setReason(e.target.value)} error={reasonError ?? undefined} maxLength={500} rows={3} />
          <ModalFooter>
            <Button type="button" variant="outline" onClick={() => setFlow(null)}>Cancel</Button>
            <Button type="button" variant="destructive" onClick={toWithdrawCode}>Continue to the authenticator code</Button>
          </ModalFooter>
      </Modal>

      <StepUpDialog
        open={flow?.kind === 'withdraw-code'}
        title="Confirm the withdrawal"
        description="New orders will no longer get this price; placed orders keep theirs."
        confirmLabel="Withdraw"
        busy={busy}
        error={codeError}
        onCancel={() => { setFlow(null); setCodeError(null); }}
        onSubmit={async (code) => { if (flow?.kind === 'withdraw-code') await withdraw(flow.reason, code); }}
      />
    </section>
  );
}
