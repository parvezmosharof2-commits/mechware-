'use client';

import * as React from 'react';
import Link from 'next/link';
import { Button } from '@mechware/ui/src/components/Button';
import { approveChangeHint, canManageUsers, userWhen, roleDiffText, userRolesText, type StaffRoleChange } from '@mechware/api-client';
import { staffUserError, staffUsersApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { FailureAlert, Loading, NoAccess, useFocusOn } from './CreditShared';
import { ReasonDialog } from './ReasonDialog';
import { StepUpDialog } from './StepUpDialog';

type Flow = { k: 'approve' | 'reject'; change: StaffRoleChange } | null;

function Queue({ meId }: { meId: string }) {
  const list = useResource(() => staffUsersApi.changes('requested'), 'requested');
  const failure = list.error ? staffUserError(list.error) : null;
  const [flow, setFlow] = React.useState<Flow>(null);
  const [busy, setBusy] = React.useState(false);
  const [codeError, setCodeError] = React.useState<string | null>(null);
  const [serverProblem, setServerProblem] = React.useState<string | null>(null);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const alertRef = useFocusOn(alertText);
  const noticeRef = React.useRef<HTMLDivElement>(null);
  React.useEffect(() => { if (notice) noticeRef.current?.focus(); }, [notice]);

  const close = () => { setFlow(null); setCodeError(null); setServerProblem(null); };
  const begin = (f: NonNullable<Flow>) => { setNotice(null); setAlertText(null); setFlow(f); };
  const refused = (err: unknown, inDialog: 'code' | 'reason') => {
    const f = staffUserError(err);
    if (inDialog === 'code' && f.stepUp) return setCodeError(f.text);
    if (inDialog === 'reason' && f.status === 400) return setServerProblem(f.text);
    close(); setAlertText(f.text);
    if (f.reload || f.status === 404 || f.status === 409) list.reload();
  };
  const approve = async (code: string) => {
    if (flow?.k !== 'approve') return;
    setBusy(true); setCodeError(null);
    try { await staffUsersApi.approve(flow.change.id, code); close(); setNotice('Approved. The roles changed and the person is signed in again with the new roles.'); list.reload(); }
    catch (err) { refused(err, 'code'); } finally { setBusy(false); }
  };
  const reject = async (reason: string) => {
    if (flow?.k !== 'reject') return;
    setBusy(true); setServerProblem(null);
    try { await staffUsersApi.reject(flow.change.id, reason); close(); setNotice('Rejected. Nothing changed.'); list.reload(); }
    catch (err) { refused(err, 'reason'); } finally { setBusy(false); }
  };
  const items = list.data?.data ?? [];

  return (
    <div className="space-y-5">
      <p className="text-sm text-text-muted">Role changes wait here until a different administrator approves them with an authenticator code. The person who asked can only withdraw a request (Reject), and nobody decides on their own roles.</p>
      {notice && <div ref={noticeRef} tabIndex={-1} role="status" data-testid="change-notice" className="rounded-md border border-surface-border bg-bg-subtle p-3 text-sm font-medium">{notice}</div>}
      {alertText && <div ref={alertRef} tabIndex={-1} role="alert" data-testid="change-alert" className="rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">{alertText}</div>}
      <FailureAlert failure={failure} onRetry={list.reload} />
      {list.loading && !list.data && <Loading />}
      {list.data && items.length === 0 && <p role="status" data-testid="changes-empty" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">No role change is waiting.</p>}
      {items.length > 0 && (
        <ul aria-label="Role change requests" className="space-y-4" data-testid="change-queue">
          {items.map((c) => {
            const hint = approveChangeHint(c, meId);
            const mine = c.requestedBy.toLowerCase() === meId.toLowerCase();
            return (
              <li key={c.id} className="space-y-2 rounded-xl border border-surface-border bg-surface p-4" data-testid="change-item">
                <h2 className="font-display text-base font-bold"><Link href={`/staff/users/${c.userId}`} className="text-brand-accent underline">Open the person</Link> <span className="text-sm font-normal text-text-muted">{roleDiffText(c.rolesBefore, c.rolesAfter)}</span></h2>
                <p className="text-sm">{userRolesText(c.rolesBefore)} <span aria-hidden="true">→</span><span className="sr-only">becomes</span> <strong>{userRolesText(c.rolesAfter)}</strong></p>
                <p className="text-sm text-text-muted">Reason: {c.reason}. Asked {userWhen(c.requestedAtUtc)}{mine ? ' (by you)' : ''}.</p>
                <div className="flex flex-wrap items-center gap-2">
                  <Button type="button" size="sm" disabled={busy || !hint.allowed} onClick={() => begin({ k: 'approve', change: c })} aria-describedby={hint.reason ? `hint-${c.id}` : undefined}>Approve</Button>
                  <Button type="button" size="sm" variant="outline" disabled={busy} onClick={() => begin({ k: 'reject', change: c })}>{mine ? 'Withdraw' : 'Reject'}</Button>
                </div>
                {hint.reason && <p id={`hint-${c.id}`} className="text-xs text-text-muted" data-testid="approve-hint">{hint.reason}</p>}
              </li>
            );
          })}
        </ul>
      )}
      <div><Button type="button" variant="ghost" size="sm" onClick={list.reload}>Refresh</Button></div>
      <StepUpDialog open={flow?.k === 'approve'} title="Approve the role change" description={flow?.k === 'approve' ? `The person ${roleDiffText(flow.change.rolesBefore, flow.change.rolesAfter)}. Their sessions end so the new roles apply at once.` : ''} confirmLabel="Approve" busy={busy} error={codeError} onCancel={close} onSubmit={approve} />
      <ReasonDialog open={flow?.k === 'reject'} title={flow?.k === 'reject' && flow.change.requestedBy.toLowerCase() === meId.toLowerCase() ? 'Withdraw this request?' : 'Reject this request?'} description="No authenticator code is needed. The roles stay as they are." label="Why? (5 to 500 characters)" confirmLabel="Reject the request" busy={busy} serverProblem={serverProblem} onCancel={close} onSubmit={reject} />
    </div>
  );
}

export function StaffRoleChangesView() {
  const { user } = useAuth();
  return (
    <ConsoleShell activeItemId="staff-role-changes" title="Role change requests">
      {canManageUsers(user?.permissions ?? []) && user ? <Queue meId={user.id} /> : <NoAccess permission="users.manage" />}
    </ConsoleShell>
  );
}
