'use client';

import * as React from 'react';
import Link from 'next/link';
import { usePathname, useRouter, useSearchParams } from 'next/navigation';
import { Badge } from '@mechware/ui/src/components/Badge';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import {
  EMPTY_USER_FILTERS, STAFF_ROLES, STAFF_ROLE_LABEL, USER_STATUSES, USER_STATUS_LABEL, activeUserFilterCount, canManageUsers, lastSignInText, parseUserFilters, twoFactorText,
  userFiltersToSearch, userRolesText, type StaffUser, type UserFilters
} from '@mechware/api-client';
import { staffUserError, staffUsersApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { FailureAlert, Loading, NoAccess } from './CreditShared';
import { Pager } from './QuoteDeskShared';

const SELECT = 'mt-1 w-full rounded-md border border-surface-border bg-bg-subtle px-3 py-2 text-sm text-text';

function List() {
  const router = useRouter();
  const pathname = usePathname();
  const search = useSearchParams().toString();
  const applied = React.useMemo(() => parseUserFilters((k) => new URLSearchParams(search).get(k)), [search]);
  const [draft, setDraft] = React.useState<UserFilters>(applied);
  // Back, forward, a shared link or a page change: the form shows what the address says.
  React.useEffect(() => setDraft(applied), [applied]);
  const go = (f: UserFilters) => { const q = userFiltersToSearch(f); router.push(q ? `${pathname}?${q}` : pathname); };
  const list = useResource(() => staffUsersApi.list(applied), search);
  const data = list.data?.data;
  const failure = list.error ? staffUserError(list.error) : null;
  const count = activeUserFilterCount(applied);

  return (
    <div className="space-y-5">
      <p className="text-sm text-text-muted">Staff accounts, found by e-mail or name. A full e-mail address also finds an account that has no staff role yet, so you can give it one. The filters are in the address.</p>
      <form role="search" aria-label="Filter staff users" className="space-y-3" noValidate onSubmit={(e) => { e.preventDefault(); go({ ...draft, page: 1 }); }}>
        <div className="grid items-end gap-3 sm:grid-cols-3">
          <Input label="E-mail or name" value={draft.q} onChange={(e) => setDraft({ ...draft, q: e.target.value })} maxLength={100} autoComplete="off" />
          <div>
            <label htmlFor="f-role" className="block text-xs font-semibold text-text-muted">Role</label>
            <select id="f-role" className={SELECT} value={draft.role} onChange={(e) => setDraft({ ...draft, role: e.target.value })}>
              <option value="">Any role</option>
              {STAFF_ROLES.map((r) => <option key={r} value={r}>{STAFF_ROLE_LABEL[r]}</option>)}
            </select>
          </div>
          <div>
            <label htmlFor="f-status" className="block text-xs font-semibold text-text-muted">Status</label>
            <select id="f-status" className={SELECT} value={draft.status} onChange={(e) => setDraft({ ...draft, status: e.target.value })}>
              <option value="">Any status</option>
              {USER_STATUSES.map((s) => <option key={s} value={s}>{USER_STATUS_LABEL[s]}</option>)}
            </select>
          </div>
        </div>
        <div className="flex flex-wrap items-center gap-3">
          <Button type="submit">Apply filters</Button>
          <Button type="button" variant="outline" disabled={count === 0 && activeUserFilterCount(draft) === 0} onClick={() => { setDraft(EMPTY_USER_FILTERS); go(EMPTY_USER_FILTERS); }}>Clear all filters</Button>
          <span className="text-xs text-text-muted" data-testid="filter-count">{count === 0 ? 'No filter applied' : `${count} ${count === 1 ? 'filter' : 'filters'} applied`}</span>
        </div>
      </form>
      <FailureAlert failure={failure} onRetry={list.reload} />
      {list.loading && !data && <Loading />}
      {data && (data.items.length === 0 ? (
        <p role="status" data-testid="users-empty" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">{count === 0 ? 'No staff account yet.' : 'No account matches these filters. Change or clear a filter.'}</p>
      ) : (
        <div className="overflow-x-auto rounded-md border border-surface-border">
          <table className="w-full text-left text-sm" data-testid="users-table">
            <caption className="sr-only">Staff users. Page {data.page}, {data.totalCount} match.</caption>
            <thead className="bg-bg-subtle text-xs text-text-muted">
              <tr>
                <th scope="col" className="px-3 py-2">Person</th>
                <th scope="col" className="px-3 py-2">Roles</th>
                <th scope="col" className="px-3 py-2">Status</th>
                <th scope="col" className="px-3 py-2">Authenticator</th>
                <th scope="col" className="px-3 py-2">Last sign-in</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((u: StaffUser) => (
                <tr key={u.id} className="border-t border-surface-border align-top" data-testid="user-row">
                  <td className="px-3 py-2"><Link href={`/staff/users/${u.id}`} className="break-all font-semibold text-brand-accent underline">{u.email}<span className="sr-only">, open the details</span></Link><span className="block text-xs text-text-muted">{u.fullName}</span></td>
                  <td className="px-3 py-2">{userRolesText(u.roles)}</td>
                  <td className="px-3 py-2"><Badge variant={u.status === 'active' ? 'success' : 'outline'}>{USER_STATUS_LABEL[u.status] ?? u.status}</Badge></td>
                  <td className="px-3 py-2">{twoFactorText(u)}</td>
                  <td className="whitespace-nowrap px-3 py-2 font-mono text-xs">{lastSignInText(u.lastSignInAtUtc)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ))}
      {data && <Pager page={data.page} total={data.totalCount} pageSize={data.pageSize} loading={list.loading} noun="people" onPage={(p) => go({ ...applied, page: p })} onRefresh={list.reload} />}
    </div>
  );
}

export function StaffUsersView() {
  const { user } = useAuth();
  return (
    <ConsoleShell activeItemId="staff-users" title="Staff users">
      {canManageUsers(user?.permissions ?? []) ? <React.Suspense fallback={<Loading />}><List /></React.Suspense> : <NoAccess permission="users.manage" />}
    </ConsoleShell>
  );
}
