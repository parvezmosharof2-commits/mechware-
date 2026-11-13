import type { MechWareApiClient } from './index';
import type { ImportCommitResult, ImportReport } from './catalog-import-model';

type Data<T> = { data: T };

/**
 * Bulk catalogue import (C10). The file goes up as multipart form data; the client sets the multipart boundary
 * itself, so no Content-Type is passed. A rejected commit is a 422 whose body carries `data` = the report.
 */
export function createCatalogImportApi(client: MechWareApiClient) {
  const form = (file: File, sellerId?: string) => {
    const body = new FormData();
    body.append('file', file);
    if (sellerId) body.append('sellerId', sellerId);
    return { method: 'POST', body } satisfies RequestInit;
  };
  return {
    dryRun: (file: File) => client.fetch<Data<ImportReport>>('/api/v1/admin/catalog/import/dry-run', form(file)),
    commit: (file: File, sellerId: string) =>
      client.fetch<Data<ImportCommitResult>>('/api/v1/admin/catalog/import/commit', form(file, sellerId))
  };
}

export type CatalogImportApi = ReturnType<typeof createCatalogImportApi>;
