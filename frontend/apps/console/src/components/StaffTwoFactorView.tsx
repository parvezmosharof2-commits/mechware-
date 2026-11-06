'use client';

import * as React from 'react';
import {
  Badge, Button, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
  EmptyState, ErrorState, Input, Skeleton
} from '@mechware/ui';
import type { StaffAwaitingTwoFactor, TwoFactorSetupCode } from '@mechware/api-client';
import { problemCode, problemMessage, problemStatus, staffApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';

function errorText(err: unknown): string {
  const status = problemStatus(err);
  const code = problemCode(err) ?? '';
  // Step-up and "not for yourself" refusals are 403s about this request, not about the role: show what the API says.
  if (status === 403 && (code.startsWith('STEP_UP_') || code === 'SETUP_CODE_FOR_SELF' || code === 'ACCOUNT_DISABLED'))
    return problemMessage(err, 'The request was refused.');
  if (status === 403) return 'Your role does not allow this action.';
  if (status === 404) return 'This account no longer exists.';
  if (status === 409 || status === 400) return problemMessage(err, 'The request was rejected.');
  return 'Something went wrong. Please try again.';
}

const dhakaTime = (utc: string) =>
  new Date(utc).toLocaleString('en-GB', { timeZone: 'Asia/Dhaka', dateStyle: 'medium', timeStyle: 'short' });

function IssueDialog({ staff, onClose, onDone }: { staff: StaffAwaitingTwoFactor | null; onClose: () => void; onDone: () => void }) {
  const [stepUpCode, setStepUpCode] = React.useState('');
  const [codeError, setCodeError] = React.useState<string | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [issued, setIssued] = React.useState<TwoFactorSetupCode | null>(null);

  React.useEffect(() => {
    setStepUpCode('');
    setCodeError(null);
    setError(null);
    setIssued(null);
  }, [staff]);

  const issue = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!staff) return;
    if (!/^\d{6}$/.test(stepUpCode.trim())) {
      setCodeError('Enter the 6-digit code your authenticator app shows now.');
      return;
    }
    setCodeError(null);
    setBusy(true);
    setError(null);
    try {
      setIssued((await staffApi.issueSetupCode(staff.userId, stepUpCode.trim())).data);
      onDone();
    } catch (err) {
      setError(errorText(err));
      // A code works once; the next attempt needs the next one.
      setStepUpCode('');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open={staff !== null} onOpenChange={(next) => !next && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Setup code for {staff?.fullName}</DialogTitle>
          <DialogDescription>
            {staff?.email}. They sign in with their password and this code, then add MECHWARE to their authenticator app.
            {staff?.hasActiveSetupCode && ' A code is already out: a new one cancels it.'}
          </DialogDescription>
        </DialogHeader>
        {issued ? (
          <div className="space-y-3">
            <div role="status" className="rounded-md border border-surface-border bg-bg-subtle p-4 text-center">
              <div className="text-xs font-semibold uppercase tracking-wide text-text-muted">Setup code</div>
              <div data-testid="issued-setup-code" className="mt-1 select-all font-mono text-2xl font-bold tracking-widest">{issued.setupCode}</div>
              <div className="mt-2 text-xs text-text-muted">Valid until {dhakaTime(issued.expiresAtUtc)} (Dhaka), for 3 tries.</div>
            </div>
            <p className="text-xs text-text-muted">
              This is the only time the code is shown. Hand it over in person or by phone, never by e-mail or chat.
            </p>
            <DialogFooter>
              <Button type="button" onClick={onClose}>Done</Button>
            </DialogFooter>
          </div>
        ) : (
          <form onSubmit={issue} noValidate className="space-y-3">
            {error && <div role="alert" className="rounded-md border border-fault bg-bg-subtle p-3 text-xs font-medium text-fault">{error}</div>}
            <Input
              label="Your authenticator code"
              helperText="Issuing a setup code needs a fresh code, even when you are signed in."
              value={stepUpCode}
              onChange={(e) => setStepUpCode(e.target.value)}
              error={codeError ?? undefined}
              inputMode="numeric"
              autoComplete="one-time-code"
            />
            <DialogFooter>
              <Button type="button" variant="outline" onClick={onClose} disabled={busy}>Cancel</Button>
              <Button type="submit" isLoading={busy}>Issue code</Button>
            </DialogFooter>
          </form>
        )}
      </DialogContent>
    </Dialog>
  );
}

function StaffTable() {
  const { user } = useAuth();
  const [selected, setSelected] = React.useState<StaffAwaitingTwoFactor | null>(null);
  const list = useResource(async () => (await staffApi.listAwaitingTwoFactor()).data, 'staff-2fa');

  return (
    <div className="space-y-4">
      <p className="text-sm text-text-muted">
        Every staff role signs in with a password and an authenticator. Staff below have none yet and cannot sign in until
        another administrator gives them a one-time setup code.
      </p>
      {list.loading ? (
        <div role="status" aria-live="polite" className="space-y-3">
          <span className="sr-only">Loading</span>
          {[0, 1].map((i) => <Skeleton key={i} className="h-14 w-full" />)}
        </div>
      ) : list.error ? (
        <ErrorState code={problemStatus(list.error) ?? 'ERR'} title="Could not load staff" description={errorText(list.error)} onRetry={list.reload} illustration={<span aria-hidden />} />
      ) : (list.data?.length ?? 0) === 0 ? (
        <EmptyState title="Everyone is set up" description="No staff member is waiting for an authenticator." />
      ) : (
        <div className="relative overflow-x-auto rounded-xl border border-surface-border">
          <table className="w-full min-w-[40rem] text-left text-sm">
            <thead className="bg-bg-subtle text-xs uppercase tracking-wide text-text-muted">
              <tr>
                <th scope="col" className="px-4 py-3">Staff member</th>
                <th scope="col" className="px-4 py-3">Roles</th>
                <th scope="col" className="px-4 py-3">Setup code</th>
                <th scope="col" className="px-4 py-3"><span className="sr-only">Actions</span></th>
              </tr>
            </thead>
            <tbody className="divide-y divide-surface-border bg-surface">
              {list.data!.map((s) => (
                <tr key={s.userId}>
                  <td className="px-4 py-3">
                    <div className="font-semibold">{s.fullName}</div>
                    <div className="text-xs text-text-muted">{s.email}</div>
                  </td>
                  <td className="px-4 py-3 text-xs">{s.roles.join(', ')}</td>
                  <td className="px-4 py-3">
                    {s.hasActiveSetupCode ? <Badge variant="low-stock">Code issued</Badge> : <span className="text-xs text-text-muted">None</span>}
                  </td>
                  <td className="px-4 py-3 text-right">
                    {s.userId === user?.id ? (
                      <span className="text-xs text-text-muted">Another admin must issue yours</span>
                    ) : (
                      <Button size="sm" variant={s.hasActiveSetupCode ? 'outline' : 'primary'} onClick={() => setSelected(s)}>
                        {s.hasActiveSetupCode ? 'Issue new code' : 'Issue code'}
                      </Button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <IssueDialog staff={selected} onClose={() => setSelected(null)} onDone={list.reload} />
    </div>
  );
}

export function StaffTwoFactorView() {
  return (
    <ConsoleShell activeItemId="staff-2fa" title="Staff authenticator setup">
      <StaffTable />
    </ConsoleShell>
  );
}
