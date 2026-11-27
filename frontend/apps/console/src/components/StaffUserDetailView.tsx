'use client';

import * as React from 'react';
import Link from 'next/link';
import { Badge } from '@mechware/ui/src/components/Badge';
import { Button } from '@mechware/ui/src/components/Button';
import { Checkbox } from '@mechware/ui/src/components/Checkbox';
import { Modal, ModalFooter } from './Modal';
import { Textarea } from '@mechware/ui/src/components/Textarea';
import {
  STAFF_ROLES, STAFF_ROLE_LABEL, USER_STATUS_LABEL, approveChangeHint, canManageUsers, userWhen, disableHint, lastSignInText, roleChangeProblems, roleDiffText, twoFactorText, userRolesText
} from '@mechware/api-client';
import { staffUserError, staffUsersApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { BackLink, FailureAlert, Loading, NoAccess, useFocusOn } from './CreditShared';
import { ReasonDialog } from './ReasonDialog';
import { StepUpDialog } from './StepUpDialog';

type Flow = { k: 'disable-reason' } | { k: 'disable-code'; reason: string } | { k: 'enable' } | { k: 'roles-form' } | { k: 'roles-code' } | null;

function Detail({ id, meId }: { id: string; meId: string }) {
  const res = useResource(() => staffUsersApi.detail(id), id);
  const failure = res.error ? staffUserError(res.error) : null;
  const user = res.data?.data.user;
  const pending = res.data?.data.pendingRoleChange ?? null;

  const [flow, setFlow] = React.useState<Flow>(null);
  const [busy, setBusy] = React.useState(false);
  const [codeError, setCodeError] = React.useState<string | null>(null);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const [chosen, setChosen] = React.useState<string[]>([]);
  const [reason, setReason] = React.useState('');
  const [problems, setProblems] = React.useState<Record<string, string>>({});
  const alertRef = useFocusOn(alertText);
  const noticeRef = React.useRef<HTMLDivElement>(null);
  React.useEffect(() => { if (notice) noticeRef.current?.focus(); }, [notice]);

  const close = () => { setFlow(null); setCodeError(null); };
  const begin = (f: NonNullable<Flow>) => { setNotice(null); setAlertText(null); setCodeError(null); setFlow(f); };
  const openRoles = () => { setChosen(user?.roles ?? []); setReason(''); setProblems({}); begin({ k: 'roles-form' }); };
  const run = async (action: () => Promise<unknown>, done: string) => {
    setBusy(true); setCodeError(null);
    try { await action(); close(); setNotice(done); res.reload(); }
    catch (err) {
      const f = staffUserError(err);
      if (f.stepUp) setCodeError(f.text); else { close(); setAlertText(f.text); if (f.reload) res.reload(); }
    } finally { setBusy(false); }
  };

  const hint = user ? disableHint(user, meId) : null;
  const approve = pending ? approveChangeHint(pending, meId) : null;
  const continueRoles = () => {
    const p = roleChangeProblems(user?.roles ?? [], chosen, reason);
    setProblems(p);
    if (Object.keys(p).length === 0) begin({ k: 'roles-code' });
  };

  return (
    <div className="space-y-5">
      <BackLink href="/staff/users">Back to staff users</BackLink>
      <FailureAlert failure={failure} onRetry={res.reload} />
      {res.loading && !res.data && <Loading />}
      {notice && <div ref={noticeRef} tabIndex={-1} role="status" data-testid="user-notice" className="rounded-md border border-surface-border bg-bg-subtle p-3 text-sm font-medium">{notice}</div>}
      {alertText && <div ref={alertRef} tabIndex={-1} role="alert" data-testid="user-alert" className="rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">{alertText}</div>}
      {user && hint && (
        <>
          <section aria-labelledby="who" className="space-y-3 rounded-xl border border-surface-border bg-surface p-4">
            <h2 id="who" className="font-display text-lg font-bold break-all">{user.email}</h2>
            <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
              <div><dt className="text-xs text-text-muted">Name</dt><dd>{user.fullName}</dd></div>
              <div><dt className="text-xs text-text-muted">Status</dt><dd data-testid="user-status"><Badge variant={user.status === 'active' ? 'success' : 'outline'}>{USER_STATUS_LABEL[user.status] ?? user.status}</Badge></dd></div>
              <div><dt className="text-xs text-text-muted">Staff roles</dt><dd data-testid="user-roles">{userRolesText(user.roles)}</dd></div>
              <div><dt className="text-xs text-text-muted">Authenticator</dt><dd>{twoFactorText(user)}</dd></div>
              <div><dt className="text-xs text-text-muted">Last sign-in</dt><dd className="font-mono text-xs">{lastSignInText(user.lastSignInAtUtc)}</dd></div>
              <div><dt className="text-xs text-text-muted">Created</dt><dd className="font-mono text-xs">{userWhen(user.createdAtUtc)}</dd></div>
            </dl>
            <div className="flex flex-wrap items-center gap-2">
              <Button type="button" variant={hint.action === 'disable' ? 'destructive' : 'primary'} disabled={busy || !hint.allowed} aria-describedby={hint.reason ? 'disable-hint' : undefined}
                onClick={() => begin(hint.action === 'disable' ? { k: 'disable-reason' } : { k: 'enable' })}>{hint.action === 'disable' ? 'Disable this account' : 'Enable this account'}</Button>
              <Button type="button" variant="outline" disabled={busy || !!pending || user.id.toLowerCase() === meId.toLowerCase()} aria-describedby="roles-hint" onClick={openRoles}>Change roles</Button>
            </div>
            {hint.reason && <p id="disable-hint" className="text-xs text-text-muted" data-testid="disable-hint">{hint.reason}</p>}
            <p id="roles-hint" className="text-xs text-text-muted">
              {user.id.toLowerCase() === meId.toLowerCase() ? 'You cannot change your own roles. Another administrator must ask for it.' : pending ? 'A role request is already open for this person. Decide it first.' : 'A role change is only a request. A different administrator approves it with an authenticator code. Disabling ends every session of the person at once.'}
            </p>
          </section>
          {pending && (
            <section aria-labelledby="pending" className="space-y-2 rounded-xl border border-surface-border bg-surface p-4" data-testid="pending-change">
              <h2 id="pending" className="font-display text-base font-bold">Open role request</h2>
              <p className="text-sm">{userRolesText(pending.rolesBefore)} <span aria-hidden="true">→</span><span className="sr-only">becomes</span> <strong>{userRolesText(pending.rolesAfter)}</strong> ({roleDiffText(pending.rolesBefore, pending.rolesAfter)})</p>
              <p className="text-sm text-text-muted">Reason: {pending.reason}. Asked {userWhen(pending.requestedAtUtc)}{pending.requestedBy.toLowerCase() === meId.toLowerCase() ? ' (by you)' : ''}.</p>
              {approve?.reason && <p className="text-xs text-text-muted">{approve.reason}</p>}
              <Link href="/staff/role-changes" className="text-sm font-semibold text-brand-accent underline">Go to the request queue</Link>
            </section>
          )}
        </>
      )}

      <ReasonDialog open={flow?.k === 'disable-reason'} title="Disable this account?" description="The person is signed out of every session at once and cannot sign in until an administrator enables the account. An authenticator code is asked next."
        label="Why? (5 to 500 characters)" confirmLabel="Continue" busy={busy} onCancel={close} onSubmit={(r) => begin({ k: 'disable-code', reason: r })} />
      <StepUpDialog open={flow?.k === 'disable-code'} title="Disable this account" description={`${user?.email ?? 'The account'} is signed out everywhere and locked out.`} confirmLabel="Disable" busy={busy} error={codeError} onCancel={close}
        onSubmit={(code) => (flow?.k === 'disable-code' ? run(() => staffUsersApi.disable(id, flow.reason, code), 'Disabled. Every session of this person ended.') : Promise.resolve())} />
      <StepUpDialog open={flow?.k === 'enable'} title="Enable this account" description={`${user?.email ?? 'The account'} can sign in again. Old sessions stay ended.`} confirmLabel="Enable" busy={busy} error={codeError} onCancel={close}
        onSubmit={(code) => run(() => staffUsersApi.enable(id, code), 'Enabled. The person can sign in again.')} />

      <Modal open={flow?.k === 'roles-form'} onOpenChange={(next) => { if (!next && !busy) close(); }} title={<>Change roles</>} description={<>Pick the full set of staff roles this person should have. Nothing changes yet: a different administrator must approve the request.</>}>
          <fieldset className="space-y-2">
            <legend className="text-xs font-semibold text-text-muted">Staff roles</legend>
            {STAFF_ROLES.map((r) => (
              <Checkbox key={r} label={STAFF_ROLE_LABEL[r]} checked={chosen.includes(r)} onCheckedChange={(v) => setChosen((c) => (v === true ? [...c.filter((x) => x !== r), r] : c.filter((x) => x !== r)))} />
            ))}
            {problems.roles && <p role="alert" className="text-xs font-medium text-fault">{problems.roles}</p>}
          </fieldset>
          <Textarea label="Reason (5 to 500 characters)" value={reason} onChange={(e) => setReason(e.target.value)} error={problems.reason} maxLength={500} rows={3} />
          <ModalFooter>
            <Button type="button" variant="outline" onClick={close}>Cancel</Button>
            <Button type="button" onClick={continueRoles}>Continue</Button>
          </ModalFooter>
      </Modal>
      <StepUpDialog open={flow?.k === 'roles-code'} title="Ask for this role change" description={user ? `${user.email} ${roleDiffText(user.roles, chosen)}.` : ''} confirmLabel="Send the request" busy={busy} error={codeError} onCancel={() => begin({ k: 'roles-form' })}
        onSubmit={(code) => run(() => staffUsersApi.requestRoles(id, chosen, reason.trim(), code), 'Request sent. It waits for a different administrator to approve it.')} />
    </div>
  );
}

export function StaffUserDetailView({ id }: { id: string }) {
  const { user } = useAuth();
  return (
    <ConsoleShell activeItemId="staff-users" title="Staff user">
      {canManageUsers(user?.permissions ?? []) && user ? <Detail id={id} meId={user.id} /> : <NoAccess permission="users.manage" />}
    </ConsoleShell>
  );
}
