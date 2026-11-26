import type { MechWareApiClient } from './index';
import type { AuditChainStatus } from './audit-model';
import { auditListQuery, type AuditDetail, type AuditFilters, type AuditPage } from './audit-read-model';

/** Audit log integrity (`/api/v1/admin/audit`). `chain` is the hash-chain check; `list` and `detail` read the log (audit.view, 60 reads a minute, each read is itself audited, values redacted by the server). */
export function createAuditApi(client: MechWareApiClient) {
  return {
    chain: () => client.fetch<{ data: AuditChainStatus }>('/api/v1/admin/audit/chain'),
    list: (f: AuditFilters) => client.fetch<{ data: AuditPage }>(`/api/v1/admin/audit?${auditListQuery(f)}`),
    detail: (id: string) => client.fetch<{ data: AuditDetail }>(`/api/v1/admin/audit/${encodeURIComponent(id)}`)
  };
}

export type AuditApi = ReturnType<typeof createAuditApi>;
