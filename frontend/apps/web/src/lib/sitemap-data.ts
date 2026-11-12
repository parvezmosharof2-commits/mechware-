import type { CategoryNode } from '@mechware/api-client';
import { productPath } from './product-ui';
import { absoluteUrl } from './site-url';
import { storefront } from './storefront-server';

/** URLs per sitemap file (the protocol allows 50,000; smaller files keep each response quick). */
export const SITEMAP_URL_LIMIT = 5000;
const PAGE_SIZE = 60; // the API clamps pageSize to 60
const MAX_PAGES_PER_CATEGORY = 200;
const TTL_MS = 60 * 60 * 1000;

export function flattenCategories(nodes: CategoryNode[]): string[] {
  return nodes.flatMap((n) => [n.slug, ...flattenCategories(n.children ?? [])]);
}

/** Product slugs reachable from the category tree, de-duplicated. A failure of one category is logged and skipped. */
async function walkProductSlugs(): Promise<string[]> {
  const tree = (await storefront.categoryTree()).data;
  const seen = new Set<string>();
  for (const category of flattenCategories(tree)) {
    try {
      for (let page = 1; page <= MAX_PAGES_PER_CATEGORY; page++) {
        const res = await storefront.categoryProducts(category, page, PAGE_SIZE);
        for (const card of res.data) seen.add(card.slug);
        if (res.data.length < PAGE_SIZE || page * PAGE_SIZE >= res.meta.totalCount) break;
      }
    } catch (error) {
      console.error(`sitemap: category "${category}" skipped`, error);
    }
  }
  return [...seen].sort();
}

let cache: { at: number; slugs: string[] } | null = null;

/** Hourly in-memory cache; the API being down yields an empty list (logged), never an error. */
export async function getProductSlugs(): Promise<string[]> {
  if (cache && Date.now() - cache.at < TTL_MS) return cache.slugs;
  try {
    const slugs = await walkProductSlugs();
    cache = { at: Date.now(), slugs };
    return slugs;
  } catch (error) {
    console.error('sitemap: API unavailable, serving the last list or an empty one', error);
    return cache?.slugs ?? [];
  }
}

/** Each product yields two URLs (en + bn), so a file holds half as many products as URLs. */
export const PRODUCTS_PER_SITEMAP = SITEMAP_URL_LIMIT / 2;

export function chunkCount(total: number): number {
  return Math.max(1, Math.ceil(total / PRODUCTS_PER_SITEMAP));
}

export function xmlEscape(value: string): string {
  return value.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&apos;');
}

/** One <url> per product in English (unprefixed) and Bangla (/bn), each carrying both hreflang alternates. */
export function productUrlsXml(slugs: string[]): string {
  const entries = slugs.flatMap((slug) => {
    const en = absoluteUrl(productPath('en', slug));
    const bn = absoluteUrl(productPath('bn', slug));
    const links =
      `<xhtml:link rel="alternate" hreflang="en" href="${xmlEscape(en)}"/>` +
      `<xhtml:link rel="alternate" hreflang="bn" href="${xmlEscape(bn)}"/>` +
      `<xhtml:link rel="alternate" hreflang="x-default" href="${xmlEscape(en)}"/>`;
    return [en, bn].map((loc) => `<url><loc>${xmlEscape(loc)}</loc>${links}</url>`);
  });
  return (
    '<?xml version="1.0" encoding="UTF-8"?>\n' +
    '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml">' +
    entries.join('') +
    '</urlset>'
  );
}

export function sitemapIndexXml(locs: string[]): string {
  return (
    '<?xml version="1.0" encoding="UTF-8"?>\n' +
    '<sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">' +
    locs.map((l) => `<sitemap><loc>${xmlEscape(l)}</loc></sitemap>`).join('') +
    '</sitemapindex>'
  );
}

/** Static pages (H1.3): one <url> per path and locale (en unprefixed, bn under /bn), each with both hreflang alternates. */
export function pagesUrlsXml(paths: string[]): string {
  const entries = paths.flatMap((p) => {
    const en = absoluteUrl(p);
    const bn = absoluteUrl(p === '/' ? '/bn' : `/bn${p}`);
    const links =
      `<xhtml:link rel="alternate" hreflang="en" href="${xmlEscape(en)}"/>` +
      `<xhtml:link rel="alternate" hreflang="bn" href="${xmlEscape(bn)}"/>` +
      `<xhtml:link rel="alternate" hreflang="x-default" href="${xmlEscape(en)}"/>`;
    return [en, bn].map((loc) => `<url><loc>${xmlEscape(loc)}</loc>${links}</url>`);
  });
  return (
    '<?xml version="1.0" encoding="UTF-8"?>\n' +
    '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml">' +
    entries.join('') +
    '</urlset>'
  );
}
