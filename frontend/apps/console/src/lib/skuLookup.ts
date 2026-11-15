import { exactSkuMatch } from '@mechware/api-client';
import { lookupsApi } from './api';

export interface ResolvedSku {
  variantId: string;
  sku: string;
  title: string;
}

/**
 * SKU to variant id through the staff lookup (`GET /admin/variants?sku=`): exact, case-insensitive, drafts and unpublished
 * products included. Returns null when no variant has exactly that SKU.
 */
export async function resolveSku(sku: string): Promise<ResolvedSku | null> {
  if (!sku.trim()) return null;
  const found = (await lookupsApi.variants({ sku: sku.trim(), pageSize: 5 })).data.items;
  const hit = exactSkuMatch(found, sku);
  return hit ? { variantId: hit.variantId, sku: hit.sku, title: hit.title } : null;
}

/** Resolves many SKUs with at most `concurrency` requests in flight; a failed lookup rejects the whole call. */
export async function resolveSkus(skus: readonly string[], concurrency = 4): Promise<Map<string, ResolvedSku | null>> {
  const unique = [...new Set(skus.map((s) => s.trim().toLowerCase()))];
  const out = new Map<string, ResolvedSku | null>();
  const original = new Map(skus.map((s) => [s.trim().toLowerCase(), s.trim()]));
  let next = 0;
  const worker = async () => {
    while (next < unique.length) {
      const key = unique[next++];
      out.set(key, await resolveSku(original.get(key) ?? key));
    }
  };
  await Promise.all(Array.from({ length: Math.min(concurrency, unique.length) }, worker));
  return out;
}
