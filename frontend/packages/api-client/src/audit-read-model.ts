/** The audit log list and detail (`GET admin/audit`, `GET admin/audit/{id}`, audit.view). Values arrive redacted by the server. */

export const AUDIT_LIST_PAGE_SIZE = 25;
/** What the server writes in place of a secret value. */
export const AUDIT_HIDDEN = '[hidden]';

export interface AuditListItem {
  id: string; createdAtUtc: string; actorId: string | null; actorEmail: string | null; actorIp: string | null;
  action: string; entityName: string; entityId: string; correlationId: string | null; chainPosition: number | null;
}
export interface AuditDetail extends AuditListItem { oldValues: unknown; newValues: unknown }
export interface AuditPage { items: AuditListItem[]; page: number; pageSize: number; totalCount: number }

export interface AuditFilters { actorId: string; action: string; entityName: string; entityId: string; correlationId: string; from: string; to: string; page: number }
export const EMPTY_AUDIT_FILTERS: AuditFilters = { actorId: '', action: '', entityName: '', entityId: '', correlationId: '', from: '', to: '', page: 1 };

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
/** A real calendar date written yyyy-MM-dd (kept here: model files cannot import each other as values under node --test). */
function isDhakaDate(text: string): boolean {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text);
  if (!m) return false;
  const d = new Date(Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3])));
  return d.getUTCFullYear() === Number(m[1]) && d.getUTCMonth() === Number(m[2]) - 1 && d.getUTCDate() === Number(m[3]);
}
const text100 = (v: string) => v.trim().slice(0, 100);

/** Reads the filters from a query string; an invalid value is dropped, so a typo in a shared link opens the plain list. */
export function parseAuditFilters(get: (key: string) => string | null): AuditFilters {
  const pick = (k: string) => (get(k) ?? '').trim();
  const actor = pick('actor'), from = pick('from'), to = pick('to'), page = Number(pick('page'));
  return {
    actorId: GUID.test(actor) ? actor : '',
    action: text100(pick('action')), entityName: text100(pick('entity')), entityId: text100(pick('entityId')), correlationId: text100(pick('correlation')),
    from: isDhakaDate(from) ? from : '', to: isDhakaDate(to) ? to : '',
    page: Number.isInteger(page) && page >= 1 && page <= 100000 ? page : 1
  };
}

function fill(f: AuditFilters, names: { actor: string; entity: string; entityId: string; correlation: string }): URLSearchParams {
  const p = new URLSearchParams();
  if (f.actorId) p.set(names.actor, f.actorId);
  if (f.action) p.set('action', f.action);
  if (f.entityName) p.set(names.entity, f.entityName);
  if (f.entityId) p.set(names.entityId, f.entityId);
  if (f.correlationId) p.set(names.correlation, f.correlationId);
  if (f.from) p.set('from', f.from);
  if (f.to) p.set('to', f.to);
  return p;
}

/** The page URL's query string (defaults left out). */
export function auditFiltersToSearch(f: AuditFilters): string {
  const p = fill(f, { actor: 'actor', entity: 'entity', entityId: 'entityId', correlation: 'correlation' });
  if (f.page > 1) p.set('page', String(f.page));
  return p.toString();
}

/** The API query string. */
export function auditListQuery(f: AuditFilters): string {
  const p = fill(f, { actor: 'actorId', entity: 'entityName', entityId: 'entityId', correlation: 'correlationId' });
  p.set('page', String(Math.max(1, Math.trunc(f.page))));
  p.set('pageSize', String(AUDIT_LIST_PAGE_SIZE));
  return p.toString();
}

/** A problem to show instead of sending, or null. */
export function auditFilterProblem(f: Omit<AuditFilters, 'page'>): string | null {
  if (f.actorId.trim() && !GUID.test(f.actorId.trim())) return 'The actor id must be a full id like 3f2a1b4c-0000-0000-0000-000000000000.';
  if ([f.action, f.entityName, f.entityId, f.correlationId].some((t) => t.trim().length > 100)) return 'Each text filter may hold at most 100 characters.';
  if (f.from && !isDhakaDate(f.from)) return 'The "from" date is not a valid date.';
  if (f.to && !isDhakaDate(f.to)) return 'The "to" date is not a valid date.';
  if (f.from && f.to && f.from > f.to) return 'The "from" date is after the "to" date.';
  return null;
}

export const activeAuditFilterCount = (f: AuditFilters): number => [f.actorId, f.action, f.entityName, f.entityId, f.correlationId, f.from, f.to].filter(Boolean).length;

/** Link to the list filtered by one correlation id. */
export const auditCorrelationHref = (correlationId: string): string => `/audit/log?${auditFiltersToSearch({ ...EMPTY_AUDIT_FILTERS, correlationId })}`;

export type AuditChange = 'added' | 'removed' | 'changed' | 'same';
export interface AuditDiffRow { path: string; before: string | null; after: string | null; change: AuditChange; hidden: boolean }

const isObj = (v: unknown): v is Record<string, unknown> | unknown[] => typeof v === 'object' && v !== null;

/** Flattens JSON into path -> shown value (objects and arrays are walked; an empty one is shown as {} or []). */
export function flattenAudit(value: unknown, prefix = '', out: Map<string, string> = new Map()): Map<string, string> {
  if (value === null || value === undefined) { if (prefix) out.set(prefix, 'null'); return out; }
  if (!isObj(value)) { out.set(prefix || '(value)', typeof value === 'string' ? value : String(value)); return out; }
  const entries: Array<[string, unknown]> = Array.isArray(value) ? value.map((v, i) => [`[${i}]`, v]) : Object.entries(value);
  if (entries.length === 0) { out.set(prefix || '(value)', Array.isArray(value) ? '[]' : '{}'); return out; }
  for (const [k, v] of entries) flattenAudit(v, prefix ? (k.startsWith('[') ? `${prefix}${k}` : `${prefix}.${k}`) : k, out);
  return out;
}

/** One row per key present in either side, in the order they first appear. A side that is null gives all rows added or removed. */
export function buildAuditDiff(oldValues: unknown, newValues: unknown): AuditDiffRow[] {
  const a = flattenAudit(oldValues ?? undefined), b = flattenAudit(newValues ?? undefined);
  return [...new Set([...a.keys(), ...b.keys()])].map((path) => {
    const before = a.has(path) ? a.get(path)! : null, after = b.has(path) ? b.get(path)! : null;
    const change: AuditChange = before === null ? 'added' : after === null ? 'removed' : before === after ? 'same' : 'changed';
    return { path, before, after, change, hidden: before === AUDIT_HIDDEN || after === AUDIT_HIDDEN };
  });
}

export const AUDIT_CHANGE_TEXT: Record<AuditChange, string> = { added: 'Added', removed: 'Removed', changed: 'Changed', same: 'Unchanged' };

export function auditChangeSummary(rows: readonly AuditDiffRow[]): string {
  const n = (c: AuditChange) => rows.filter((r) => r.change === c).length;
  return `${n('changed')} changed, ${n('added')} added, ${n('removed')} removed, ${n('same')} unchanged`;
}

/** Failure text for the list and detail calls. */
export function auditReadErrorText(f: { status: number | null }): string {
  switch (f.status) {
    case 400: return 'One of the filters is not valid. Check the filters and try again.';
    case 401: return 'Your session has ended. Sign in again.';
    case 403: return 'Your role does not allow viewing the audit log (audit.view).';
    case 404: return 'This audit row does not exist.';
    case 429: return 'You have read the audit log too often (limit: 60 reads a minute). Wait a minute and try again.';
    default: return 'The audit log could not be read. Please try again.';
  }
}
