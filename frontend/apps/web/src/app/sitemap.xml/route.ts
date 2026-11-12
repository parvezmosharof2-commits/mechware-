import { chunkCount, getProductSlugs, sitemapIndexXml } from '../../lib/sitemap-data';
import { absoluteUrl } from '../../lib/site-url';

// Built per request (never prerendered, so an API outage cannot fail `next build`); the slug list is cached for an hour.
export const dynamic = 'force-dynamic';
export const revalidate = 3600;

/** Sitemap index: the static pages file first, then one file per block of products. */
export async function GET() {
  const slugs = await getProductSlugs();
  const locs = [absoluteUrl('/sitemaps/pages.xml'), ...Array.from({ length: chunkCount(slugs.length) }, (_, i) => absoluteUrl(`/sitemaps/products-${i + 1}.xml`))];
  return new Response(sitemapIndexXml(locs), {
    headers: { 'Content-Type': 'application/xml; charset=utf-8', 'Cache-Control': 'public, max-age=0, s-maxage=3600' }
  });
}
