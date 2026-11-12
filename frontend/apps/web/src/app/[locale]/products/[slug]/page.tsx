import type { Metadata } from 'next';
import Link from 'next/link';
import { notFound, permanentRedirect } from 'next/navigation';
import { getLocale, getTranslations } from 'next-intl/server';
import { ProductView } from '../../../../components/product/ProductView';
import { breadcrumbLabel, localePath, pickLocalized, productPath, productTitle } from '../../../../lib/product-ui';
import { PUBLIC_API_BASE_URL, loadProduct } from '../../../../lib/storefront-server';

type Props = {
  params: Promise<{ locale: string; slug: string }>;
  searchParams: Promise<{ v?: string | string[] }>;
};

export async function generateMetadata({ params }: Pick<Props, 'params'>): Promise<Metadata> {
  const { slug } = await params;
  const result = await loadProduct(decodeURIComponent(slug));
  if (result.kind !== 'ok') return { title: 'Mechware', robots: { index: false } };
  const { product } = result;
  const locale = await getLocale();
  const title = productTitle(product, locale);
  const description = pickLocalized(locale, product.shortDescriptionEn, product.shortDescriptionBn);
  return {
    title: `${title} | Mechware`,
    description: description || undefined,
    alternates: {
      canonical: productPath(locale, product.slug),
      languages: { en: productPath('en', product.slug), bn: productPath('bn', product.slug), 'x-default': productPath('en', product.slug) }
    }
  };
}

export default async function ProductPage({ params, searchParams }: Props) {
  const { slug: rawSlug } = await params;
  const slug = decodeURIComponent(rawSlug);
  const { v } = await searchParams;
  const locale = await getLocale();

  const result = await loadProduct(slug);
  if (result.kind === 'not-found') notFound();
  // Not a 404: the error boundary shows "temporarily unavailable" with a retry.
  if (result.kind === 'error') throw new Error(`Product API answered ${result.status}`);

  const { product } = result;
  // The API followed a 301 for a retired slug: send the browser to the canonical address (keeping the chosen variant).
  if (product.slug !== slug) {
    const sku = Array.isArray(v) ? v[0] : v;
    permanentRedirect(`${productPath(locale, product.slug)}${sku ? `?v=${encodeURIComponent(sku)}` : ''}`);
  }

  const t = await getTranslations('Product');
  const crumbs = product.breadcrumbs;

  return (
    <main className="mx-auto max-w-7xl px-4 py-6">
      <nav aria-label={t('breadcrumb')} className="mb-4 text-sm">
        <ol className="flex flex-wrap items-center gap-x-2 gap-y-1 text-text-muted">
          <li>
            <Link href={localePath(locale, '/')} className="hover:text-accent">
              {t('home')}
            </Link>
          </li>
          {crumbs.map((c) => (
            <li key={c.slug} className="flex items-center gap-2">
              <span aria-hidden="true">/</span>
              <Link href={`${localePath(locale, '/search')}?category=${encodeURIComponent(c.slug)}`} className="hover:text-accent">
                {breadcrumbLabel(c, locale)}
              </Link>
            </li>
          ))}
          <li className="flex items-center gap-2" aria-current="page">
            <span aria-hidden="true">/</span>
            <span className="text-text">{productTitle(product, locale)}</span>
          </li>
        </ol>
      </nav>
      <ProductView product={product} initialSku={Array.isArray(v) ? v[0] : v} mediaBase={PUBLIC_API_BASE_URL} />
    </main>
  );
}
