/** The audit log's tamper check (`GET /api/v1/admin/audit/chain`, permission audit.view). Hand-written from AdminAuditController. */

export interface AuditChainStatus {
  /** Rows sealed into the hash chain. */
  sealed: number;
  /** Rows too new to be sealed yet (they are sealed after a settle period). */
  unsealed: number;
  intact: boolean;
  /** Position of the first row that does not match; null when the chain is intact. */
  firstBrokenPosition: number | null;
  /** Hash of the last sealed row (hex); null when nothing is sealed. */
  headHash: string | null;
}

export const AUDIT_PERMISSION = 'audit.view';
/** The server serves one check to everyone for this many minutes (AdminAuditController.CacheFor). */
export const AUDIT_CHECK_CACHE_MINUTES = 5;

export const canViewAudit = (permissions: readonly string[]): boolean => permissions.some((p) => p.toLowerCase() === AUDIT_PERMISSION);

export type AuditVerdict = 'intact' | 'broken' | 'empty';

export function auditVerdict(s: AuditChainStatus): AuditVerdict {
  if (!s.intact || s.firstBrokenPosition !== null) return 'broken';
  return s.sealed === 0 ? 'empty' : 'intact';
}

/** One plain sentence for the verdict, safe to read aloud. */
export function auditVerdictText(s: AuditChainStatus): string {
  switch (auditVerdict(s)) {
    case 'broken':
      return s.firstBrokenPosition === null
        ? 'The audit log does not match its seal. Treat this as a security incident.'
        : `The audit log does not match its seal, starting at row ${s.firstBrokenPosition}. Treat this as a security incident.`;
    case 'empty':
      return 'No rows are sealed yet, so there is nothing to check.';
    default:
      return `All ${s.sealed.toLocaleString('en-US')} sealed rows match their seal. No row was changed, removed or inserted.`;
  }
}

/** A hash as groups of 8 characters so it can be compared by eye; empty input gives an em dash. */
export function groupHash(hash: string | null): string {
  if (!hash) return '—';
  return hash.replace(/\s+/g, '').match(/.{1,8}/g)?.join(' ') ?? '—';
}

/** Failure text for the check call. */
export function auditErrorText(f: { status: number | null }): string {
  if (f.status === 403) return 'Your role does not allow viewing the audit log (audit.view).';
  if (f.status === 401) return 'Your session has ended. Sign in again.';
  if (f.status === 429) return 'Too many checks. Wait a minute and try again.';
  return 'The check could not be read. Please try again.';
}
