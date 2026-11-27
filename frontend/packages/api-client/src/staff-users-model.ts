/** Models stay free of relative imports (node --test runs them as they are), so the two small helpers are local. */
const reasonProblem = (reason: string): string | null => {
  const n = reason.trim().length;
  if (n === 0) return 'Enter a reason.';
  return n < 5 || n > 500 ? 'The reason must be 5 to 500 characters.' : null;
};
const dhakaStamp = (iso: string | null | undefined): string => {
  const ms = iso ? Date.parse(/(Z|[+-]\d\d:?\d\d)$/.test(iso) ? iso : `${iso}Z`) : Number.NaN;
  return Number.isFinite(ms) ? `${new Date(ms + 6 * 3600 * 1000).toISOString().replace('T', ' ').slice(0, 16)} Dhaka` : '';
};

/** Staff users and roles (G5.7 backend, G5.8 screens). The 4 staff roles are the code names, not the brief's. */
export const STAFF_ROLES = ['support', 'finance', 'moderator', 'admin'] as const;
export type StaffRoleName = (typeof STAFF_ROLES)[number];
export const STAFF_ROLE_LABEL: Record<StaffRoleName, string> = { support: 'Support', finance: 'Finance', moderator: 'Moderator', admin: 'Admin' };
export const USER_STATUSES = ['active', 'pending_verification', 'suspended', 'banned'] as const;
export const USER_STATUS_LABEL: Record<string, string> = { active: 'Active', pending_verification: 'Waiting for e-mail check', suspended: 'Disabled', banned: 'Banned' };

export interface StaffUser {
  id: string; email: string; fullName: string; roles: string[]; status: string; twoFactorEnrolled: boolean; lastSignInAtUtc: string | null; createdAtUtc: string;
}
export interface StaffRoleChange {
  id: string; userId: string; rolesBefore: string[]; rolesAfter: string[]; reason: string; status: string; requestedBy: string; requestedAtUtc: string;
  decidedBy: string | null; decidedAtUtc: string | null; decisionReason: string | null;
}
export interface StaffUserPage { items: StaffUser[]; page: number; pageSize: number; totalCount: number }
export interface StaffUserDetail { user: StaffUser; pendingRoleChange: StaffRoleChange | null }
export interface StaffRole { name: string; permissions: string[] }

export const canManageUsers = (permissions: readonly string[]): boolean => permissions.some((p) => p.toLowerCase() === 'users.manage');

export interface UserFilters { q: string; role: string; status: string; page: number }
export const EMPTY_USER_FILTERS: UserFilters = { q: '', role: '', status: '', page: 1 };
const oneOf = (v: string | null, list: readonly string[]) => (v && list.includes(v) ? v : '');

/** Reads the filters from the address; an unknown role or status is dropped, a bad page is 1. */
export function parseUserFilters(get: (key: string) => string | null): UserFilters {
  const page = Number.parseInt(get('page') ?? '', 10);
  return { q: (get('q') ?? '').trim().slice(0, 100), role: oneOf(get('role'), STAFF_ROLES), status: oneOf(get('status'), USER_STATUSES), page: Number.isFinite(page) && page >= 1 ? page : 1 };
}
export function userFiltersToSearch(f: UserFilters): string {
  const p = new URLSearchParams();
  if (f.q.trim()) p.set('q', f.q.trim());
  if (f.role) p.set('role', f.role);
  if (f.status) p.set('status', f.status);
  if (f.page > 1) p.set('page', String(f.page));
  return p.toString();
}
export const userListQuery = (f: UserFilters): string => {
  const p = new URLSearchParams(userFiltersToSearch(f));
  p.set('page', String(f.page));
  p.set('pageSize', '25');
  return p.toString();
};
export const activeUserFilterCount = (f: UserFilters): number => [f.q.trim(), f.role, f.status].filter(Boolean).length;

export const userRolesText = (roles: readonly string[]): string => (roles.length === 0 ? 'No staff role' : roles.map((r) => STAFF_ROLE_LABEL[r as StaffRoleName] ?? r).join(', '));
export const twoFactorText = (u: Pick<StaffUser, 'twoFactorEnrolled'>): string => (u.twoFactorEnrolled ? 'Authenticator set up' : 'No authenticator yet');
export const userWhen = dhakaStamp;
export const lastSignInText = (iso: string | null): string => (iso ? dhakaStamp(iso) || 'unknown' : 'Never signed in');

/** What the disable/enable button may do for this person; the words are what the API would refuse with. */
export function disableHint(user: Pick<StaffUser, 'id' | 'status'>, meId: string): { allowed: boolean; action: 'disable' | 'enable'; reason: string | null } {
  if (user.status === 'suspended') return { allowed: true, action: 'enable', reason: null };
  if (user.id.toLowerCase() === meId.toLowerCase()) return { allowed: false, action: 'disable', reason: 'You cannot disable your own account. Ask another administrator.' };
  return { allowed: true, action: 'disable', reason: null };
}

/** The requester may not approve (reject = withdraw); four eyes means a different administrator decides, and never the person concerned. */
export function approveChangeHint(c: Pick<StaffRoleChange, 'requestedBy' | 'userId' | 'status'>, meId: string): { allowed: boolean; reason: string | null } {
  if (c.status !== 'requested') return { allowed: false, reason: 'This request is already decided.' };
  if (c.userId.toLowerCase() === meId.toLowerCase()) return { allowed: false, reason: 'These are your own roles. Nobody approves a change to their own roles; another administrator must.' };
  if (c.requestedBy.toLowerCase() === meId.toLowerCase()) return { allowed: false, reason: 'You asked for this change. Four eyes: a different administrator must approve it. You can withdraw it with Reject.' };
  return { allowed: true, reason: null };
}

export const sameRoles = (a: readonly string[], b: readonly string[]): boolean => a.length === b.length && [...a].sort().join() === [...b].sort().join();
export function roleChangeProblems(current: readonly string[], chosen: readonly string[], reason: string): Record<string, string> {
  const out: Record<string, string> = {};
  if (sameRoles(current, chosen)) out.roles = 'Choose a different set of roles than the person has now.';
  const r = reasonProblem(reason);
  if (r) out.reason = r;
  return out;
}
export const roleDiffText = (before: readonly string[], after: readonly string[]): string => {
  const add = after.filter((r) => !before.includes(r));
  const drop = before.filter((r) => !after.includes(r));
  const name = (r: string) => STAFF_ROLE_LABEL[r as StaffRoleName] ?? r;
  return [add.length ? `adds ${add.map(name).join(', ')}` : '', drop.length ? `removes ${drop.map(name).join(', ')}` : ''].filter(Boolean).join(' and ') || 'no change';
};

const TEXT: Record<string, string> = {
  USER_DISABLE_SELF: 'You cannot disable your own account. Ask another administrator.',
  LAST_ADMIN: 'This would leave no other active administrator with an authenticator. Set one up or keep this person first.',
  ROLE_CHANGE_SELF: 'Nobody asks for, approves or rejects a change of their own roles. Ask another administrator.',
  ROLE_CHANGE_FOUR_EYES: 'Four eyes: the person who asked for a role change cannot approve it. A different administrator must.',
  ROLE_CHANGE_PENDING: 'This person already has an open role request. Decide it first.',
  ROLE_CHANGE_STALE: 'The roles changed since this request was made. Reject it and ask again.',
  NOT_STAFF_ACCOUNT: 'This account is not a staff account.',
  STEP_UP_FAILED: 'That authenticator code was not accepted. Try the next code.',
  STEP_UP_REQUIRED: 'An authenticator code is needed for this.'
};
export const USER_RELOAD_CODES = new Set(['ROLE_CHANGE_STALE', 'ROLE_CHANGE_PENDING', 'ROLE_CHANGE_FOUR_EYES', 'LAST_ADMIN']);
export function staffUserErrorText(e: { status: number | null; code: string | null; message?: string }): string {
  if (e.code && TEXT[e.code]) return TEXT[e.code];
  if (e.status === null) return 'The server cannot be reached. Check your connection and try again.';
  if (e.status === 401) return 'Your session ended. Sign in again.';
  if (e.status === 403) return 'Your role does not allow this.';
  if (e.status === 404) return 'This account or request no longer exists.';
  if (e.status === 429) return 'Too many tries. Wait a minute and try again.';
  if (e.status === 400 && e.message) return e.message;
  return 'Something went wrong on our side. Try again.';
}
