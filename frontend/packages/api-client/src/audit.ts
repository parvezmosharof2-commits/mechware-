import type { MechWareApiClient } from './index';
import type { AuditChainStatus } from './audit-model';

/** Audit log integrity (`/api/v1/admin/audit`). Only the hash-chain check exists today; there is no list or search endpoint yet. */
export function createAuditApi(client: MechWareApiClient) {
  return {
    chain: () => client.fetch<{ data: AuditChainStatus }>('/api/v1/admin/audit/chain')
  };
}

export type AuditApi = ReturnType<typeof createAuditApi>;
