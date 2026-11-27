import type { MechWareApiClient } from './index';
import { userListQuery, type StaffRole, type StaffRoleChange, type StaffUser, type StaffUserDetail, type StaffUserPage, type UserFilters } from './staff-users-model';

type Data<T> = { data: T };

/** Staff users and roles (`/api/v1/admin`, users.manage). Disable, enable, request and approve carry an authenticator code in `X-Step-Up-Code`; reject does not. */
export function createStaffUsersApi(client: MechWareApiClient) {
  const base = '/api/v1/admin';
  const id = (v: string) => encodeURIComponent(v);
  const code = (c: string) => ({ 'X-Step-Up-Code': c.trim() });
  return {
    list: (f: UserFilters) => client.fetch<Data<StaffUserPage>>(`${base}/users?${userListQuery(f)}`),
    detail: (userId: string) => client.fetch<Data<StaffUserDetail>>(`${base}/users/${id(userId)}`),
    roles: () => client.fetch<Data<StaffRole[]>>(`${base}/roles`),
    disable: (userId: string, reason: string, stepUp: string) =>
      client.fetch<Data<StaffUser>>(`${base}/users/${id(userId)}/disable`, { method: 'POST', body: JSON.stringify({ reason }), headers: code(stepUp) }),
    enable: (userId: string, stepUp: string, reason?: string) =>
      client.fetch<Data<StaffUser>>(`${base}/users/${id(userId)}/enable`, { method: 'POST', body: JSON.stringify({ reason: reason ?? null }), headers: code(stepUp) }),
    requestRoles: (userId: string, roles: string[], reason: string, stepUp: string) =>
      client.fetch<Data<StaffRoleChange>>(`${base}/users/${id(userId)}/roles`, { method: 'PUT', body: JSON.stringify({ roles, reason }), headers: code(stepUp) }),
    changes: (status: 'requested' | 'approved' | 'rejected' = 'requested') => client.fetch<Data<StaffRoleChange[]>>(`${base}/role-changes?status=${status}`),
    approve: (changeId: string, stepUp: string) => client.fetch<Data<StaffRoleChange>>(`${base}/role-changes/${id(changeId)}/approve`, { method: 'POST', headers: code(stepUp) }),
    reject: (changeId: string, reason: string) => client.fetch<Data<StaffRoleChange>>(`${base}/role-changes/${id(changeId)}/reject`, { method: 'POST', body: JSON.stringify({ reason }) })
  };
}
export type StaffUsersApi = ReturnType<typeof createStaffUsersApi>;
