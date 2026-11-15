import { storefrontApi } from './api';

export interface ResolvedSku {
  variantId: string;
  sku: string;
  title: string;
}

/**
 * SKU to variant id. There is no admin SKU lookup yet, so this goes through the public search (SKUs are indexed as part
 * numbers) and then the product page to read the variant id. Only published products are found.
 * Returns null when no variant has exactly that SKU.
 */
export async function resolveSku(sku: string): Promise<ResolvedSku | null> {
  const wanted = sku.trim().toLowerCase();
  if (!wanted) return null;
  const found = await storefrontApi.search({ q: sku.trim(), pageSize: 5 });
  const hits = found.data.filter((h) => h.partNumbers.some((p) => p.toLowerCase() === wanted));
  for (const hit of hits) {
    const product = (await storefrontApi.productBySlug(hit.slug)).data;
    const variant = product.variants.find((v) => v.sku.toLowerCase() === wanted);
    if (variant) return { variantId: variant.id, sku: variant.sku, title: product.titleEn };
  }
  return null;
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
