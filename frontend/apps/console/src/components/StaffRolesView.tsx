'use client';

import * as React from 'react';
import { STAFF_ROLE_LABEL, canManageUsers, type StaffRoleName } from '@mechware/api-client';
import { staffUserError, staffUsersApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { FailureAlert, Loading, NoAccess } from './CreditShared';

function Roles() {
  const res = useResource(() => staffUsersApi.roles(), 'roles');
  const failure = res.error ? staffUserError(res.error) : null;
  return (
    <div className="space-y-5">
      <p className="text-sm text-text-muted">Read only. What each staff role may do is set in the code and changes only with a new release. A person&apos;s roles are changed on their page, with a second administrator approving.</p>
      <FailureAlert failure={failure} onRetry={res.reload} />
      {res.loading && !res.data && <Loading />}
      {res.data && (
        <div className="grid gap-4 md:grid-cols-2" data-testid="roles-list">
          {res.data.data.map((r) => (
            <section key={r.name} aria-labelledby={`role-${r.name}`} className="space-y-2 rounded-xl border border-surface-border bg-surface p-4" data-testid="role-card">
              <h2 id={`role-${r.name}`} className="font-display text-base font-bold">{STAFF_ROLE_LABEL[r.name as StaffRoleName] ?? r.name} <span className="font-mono text-xs font-normal text-text-muted">({r.name})</span></h2>
              <ul className="flex flex-wrap gap-1.5" aria-label={`Permissions of ${r.name}`}>
                {[...r.permissions].sort().map((p) => <li key={p} className="rounded-sm bg-bg-subtle px-1.5 py-0.5 font-mono text-xs">{p}</li>)}
              </ul>
            </section>
          ))}
        </div>
      )}
    </div>
  );
}

export function StaffRolesView() {
  const { user } = useAuth();
  return (
    <ConsoleShell activeItemId="staff-roles" title="Roles and permissions">
      {canManageUsers(user?.permissions ?? []) ? <Roles /> : <NoAccess permission="users.manage" />}
    </ConsoleShell>
  );
}
