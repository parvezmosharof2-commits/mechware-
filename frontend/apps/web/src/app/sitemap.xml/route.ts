import { chunkCount, getProductSlugs, sitemapIndexXml } from '../../lib/sitemap-data';
import { absoluteUrl } from '../../lib/site-url';

// Built per request (never prerendered, so an API outage cannot fail `next build`); the slug list is cached for an hour.
export const dynamic = 'force-dynamic';
export const revalidate = 3600;

/** Sitemap index. Static pages (H1.3) add their own entry next to the product files. */
export async function GET() {
  const slugs = await getProductSlugs();
  const locs = Array.from({ length: chunkCount(slugs.length) }, (_, i) => absoluteUrl(`/sitemaps/products-${i + 1}.xml`));
  return new Response(sitemapIndexXml(locs), {
    headers: { 'Content-Type': 'application/xml; charset=utf-8', 'Cache-Control': 'public, max-age=0, s-maxage=3600' }
  });
}
