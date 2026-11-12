import type * as React from 'react';
import type { Metadata } from 'next';
import { getLocale, getTranslations } from 'next-intl/server';
import {
  MechWareApiClient,
  createStorefrontApi,
  fromSearchParams,
  problemStatus,
  type SearchResponse
} from '@mechware/api-client';
import { SearchView } from '../../../components/search/SearchView';
import { ResultsTable } from '../../../components/search/ResultsTable';
import { ResultsList } from '../../../components/search/ResultsList';

// Public, anonymous call made on the server: no session token, no refresh, so it never touches the browser token vault.
const API_BASE_URL = process.env.API_INTERNAL_BASE_URL ?? process.env.NEXT_PUBLIC_API_BASE_URL ?? 'http://localhost:5000';
const storefront = createStorefrontApi(
  new MechWareApiClient({ baseUrl: API_BASE_URL, getAccessToken: () => null, autoRefreshOn401: false })
);

type RawSearchParams = Record<string, string | string[] | undefined>;

function toUrlSearchParams(raw: RawSearchParams): URLSearchParams {
  const out = new URLSearchParams();
  for (const [key, value] of Object.entries(raw)) {
    if (Array.isArray(value)) value.forEach((v) => out.append(key, v));
    else if (value !== undefined) out.set(key, value);
  }
  return out;
}

export async function generateMetadata({ searchParams }: { searchParams: Promise<RawSearchParams> }): Promise<Metadata> {
  const t = await getTranslations('Search');
  const { q } = fromSearchParams(toUrlSearchParams(await searchParams));
  return { title: q ? t('metaTitleQuery', { q }) : t('metaTitle'), robots: { index: false, follow: true } };
}

export default async function SearchPage({ searchParams }: { searchParams: Promise<RawSearchParams> }) {
  const params = fromSearchParams(toUrlSearchParams(await searchParams));

  let result: SearchResponse | null = null;
  let errorStatus: number | null = null;
  // Brand names label the facet slugs; the list is cached by the API, and a failure only costs the nicer labels.
  const brandsPromise = storefront.brands().then((r) => r.data, () => []);
  try {
    result = await storefront.search(params);
  } catch (error) {
    // Network failures carry no status; they read as "search unavailable" like a 503.
    errorStatus = problemStatus(error) ?? 503;
  }

  const brandLabels = Object.fromEntries((await brandsPromise).map((b) => [b.slug, b.name]));
  const hits = result?.data ?? [];
  let results: React.ReactNode = null;
  if (hits.length > 0) {
    const [t, locale] = await Promise.all([getTranslations('Search'), getLocale()]);
    const highlight = result?.meta.matchedBy === 'partNumber';
    results = (
      <>
        <ResultsTable hits={hits} locale={locale} query={params.q} highlight={highlight} t={t} />
        <ResultsList hits={hits} locale={locale} query={params.q} highlight={highlight} t={t} />
      </>
    );
  }
  const slim = result && { meta: result.meta, data: result.data.map((h) => ({ brandSlug: h.brandSlug, brandName: h.brandName })) };
  return <SearchView params={params} result={slim} results={results} errorStatus={errorStatus} brandLabels={brandLabels} />;
}
