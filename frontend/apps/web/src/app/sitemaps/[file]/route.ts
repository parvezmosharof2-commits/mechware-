import { PRODUCTS_PER_SITEMAP, getProductSlugs, productUrlsXml } from '../../../lib/sitemap-data';

export const dynamic = 'force-dynamic';
export const revalidate = 3600;

/** `/sitemaps/products-<n>.xml`: the n-th block (1-based) of at most 2,500 products = 5,000 URLs. */
export async function GET(_request: Request, { params }: { params: Promise<{ file: string }> }) {
  const { file } = await params;
  const match = /^products-(\d{1,4})\.xml$/.exec(file);
  if (!match) return new Response('Not found', { status: 404 });
  const index = Number(match[1]) - 1;
  const slugs = (await getProductSlugs()).slice(index * PRODUCTS_PER_SITEMAP, (index + 1) * PRODUCTS_PER_SITEMAP);
  if (index > 0 && slugs.length === 0) return new Response('Not found', { status: 404 });
  return new Response(productUrlsXml(slugs), {
    headers: { 'Content-Type': 'application/xml; charset=utf-8', 'Cache-Control': 'public, max-age=0, s-maxage=3600' }
  });
}
