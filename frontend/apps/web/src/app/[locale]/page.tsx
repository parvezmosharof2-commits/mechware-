import type { Metadata } from 'next';
import Link from 'next/link';
import { getLocale, getTranslations } from 'next-intl/server';
import type { SearchResponse } from '@mechware/api-client';
import { SearchForm } from '../../components/layout/SiteHeader';
import { ProductStrip } from '../../components/home/ProductStrip';
import { allBrands, storefront, topCategories } from '../../lib/storefront-server';
import { jsonLdString } from '../../lib/json-ld';
import { absoluteUrl } from '../../lib/site-url';

export async function generateMetadata(): Promise<Metadata> {
  const [t, locale] = await Promise.all([getTranslations('Home'), getLocale()]);
  return {
    title: t('metaTitle'),
    description: t('metaDescription'),
    alternates: { canonical: locale === 'bn' ? '/bn' : '/', languages: { en: '/', bn: '/bn', 'x-default': '/' } }
  };
}

export default async function HomePage() {
  const [t, ts, locale] = await Promise.all([getTranslations('Home'), getTranslations('Search'), getLocale()]);
  const loc: 'en' | 'bn' = locale === 'bn' ? 'bn' : 'en';
  const prefix = loc === 'bn' ? '/bn' : '';

  const [categories, brands, best] = await Promise.all([
    topCategories(),
    allBrands(),
    storefront.search({ sort: 'most-sold', pageSize: 8 }).catch((): SearchResponse | null => null)
  ]);
  const hits = best?.data ?? [];
  // Example chips are real part numbers from the catalogue, never typed in by hand.
  const examples = [...new Set(hits.map((h) => h.partNumbers[0]).filter((p): p is string => Boolean(p)))].slice(0, 4);

  const jsonLd = [
    { '@context': 'https://schema.org', '@type': 'Organization', name: 'MECHWARE', url: absoluteUrl('/') },
    {
      '@context': 'https://schema.org',
      '@type': 'WebSite',
      name: 'MECHWARE',
      url: absoluteUrl('/'),
      inLanguage: ['en', 'bn'],
      potentialAction: {
        '@type': 'SearchAction',
        target: { '@type': 'EntryPoint', urlTemplate: `${absoluteUrl('/search')}?q={search_term_string}` },
        'query-input': 'required name=search_term_string'
      }
    }
  ];

  // Quotes and credit/VAT invoices are not built yet, so they are labelled as coming soon.
  const valueProps: { key: string; soon: boolean }[] = [
    { key: 'search', soon: false },
    { key: 'company', soon: false },
    { key: 'quotes', soon: true },
    { key: 'credit', soon: true }
  ];
  const name = (n: { nameEn: string; nameBn: string }) => (loc === 'bn' && n.nameBn.trim() ? n.nameBn : n.nameEn);

  return (
    <main id="main">
      <script type="application/ld+json" dangerouslySetInnerHTML={{ __html: jsonLdString(jsonLd) }} />

      <section className="border-b border-surface-border bg-bg-subtle">
        <div className="mx-auto max-w-4xl px-4 py-12 text-center md:py-20">
          <h1 className="font-display text-3xl font-extrabold tracking-tight text-text md:text-5xl">{t('heroTitle')}</h1>
          <p className="mx-auto mt-3 max-w-2xl text-text-muted md:text-lg">{t('heroSubtitle')}</p>
          <div className="mx-auto mt-8 max-w-2xl">
            <SearchForm locale={loc} id="hero-search" large placeholder={t('searchPlaceholder')} label={t('searchLabel')} button={t('searchButton')} />
          </div>
          <p className="mt-3 text-sm text-text-muted">{t('searchHelper')}</p>
          {examples.length > 0 && (
            <div className="mt-3 flex flex-wrap items-center justify-center gap-2">
              <span className="text-sm text-text-muted">{t('tryExamples')}</span>
              {examples.map((p) => (
                <Link
                  key={p}
                  href={`${prefix}/search?q=${encodeURIComponent(p)}`}
                  className="rounded-full border border-surface-border bg-bg px-3 py-1 font-mono text-sm text-text hover:border-accent hover:text-accent"
                >
                  {p}
                </Link>
              ))}
            </div>
          )}
        </div>
      </section>

      {categories.length > 0 && (
        <section aria-labelledby="home-categories" className="mx-auto max-w-7xl px-4 pt-12">
          <h2 id="home-categories" className="font-display text-2xl font-bold text-text">{t('categoriesTitle')}</h2>
          <ul className="mt-4 grid grid-cols-1 gap-3 min-[480px]:grid-cols-2 lg:grid-cols-3">
            {categories.map((c) => (
              <li key={c.id}>
                <Link
                  href={`${prefix}/search?category=${encodeURIComponent(c.slug)}`}
                  className="block h-full rounded-lg border border-surface-border bg-bg-subtle p-4 transition-colors hover:border-accent"
                >
                  <span className="font-semibold text-text">{name(c)}</span>
                  {c.children.length > 0 && (
                    <span className="mt-1 block text-sm text-text-muted">{c.children.slice(0, 3).map(name).join(' · ')}</span>
                  )}
                </Link>
              </li>
            ))}
          </ul>
        </section>
      )}

      {brands.length > 0 && (
        <section aria-labelledby="home-brands" className="mx-auto max-w-7xl px-4 pt-12">
          <h2 id="home-brands" className="font-display text-2xl font-bold text-text">{t('brandsTitle')}</h2>
          <ul className="mt-4 flex flex-wrap gap-2">
            {brands.map((b) => (
              <li key={b.slug}>
                <Link
                  href={`${prefix}/search?brand=${encodeURIComponent(b.slug)}`}
                  className="inline-block rounded-md border border-surface-border bg-bg-subtle px-4 py-2 text-sm font-semibold text-text hover:border-accent hover:text-accent"
                >
                  {b.name}
                </Link>
              </li>
            ))}
          </ul>
        </section>
      )}

      {hits.length > 0 && (
        <section aria-labelledby="home-best" className="mx-auto max-w-7xl px-4 pt-12">
          <div className="flex items-end justify-between gap-4">
            <h2 id="home-best" className="font-display text-2xl font-bold text-text">{t('bestSellersTitle')}</h2>
            <Link prefetch={false} href={`${prefix}/search?sort=most-sold`} className="text-sm font-semibold text-accent hover:underline">{t('viewAll')}</Link>
          </div>
          <div className="mt-4">
            <ProductStrip hits={hits} locale={loc} t={ts} />
          </div>
        </section>
      )}

      <section aria-labelledby="home-why" className="mx-auto max-w-7xl px-4 pt-12">
        <h2 id="home-why" className="font-display text-2xl font-bold text-text">{t('whyTitle')}</h2>
        <ul className="mt-4 grid grid-cols-1 gap-3 min-[480px]:grid-cols-2 lg:grid-cols-4">
          {valueProps.map(({ key, soon }) => (
            <li key={key} className="rounded-lg border border-surface-border bg-bg-subtle p-4">
              <h3 className="font-semibold text-text">
                {t(`value.${key}.title`)}
                {soon && (
                  <span className="ml-2 rounded-full border border-surface-border px-2 py-0.5 align-middle text-xs font-medium text-text-muted">
                    {t('comingSoon')}
                  </span>
                )}
              </h3>
              <p className="mt-1 text-sm text-text-muted">{t(`value.${key}.body`)}</p>
            </li>
          ))}
        </ul>
      </section>
    </main>
  );
}
