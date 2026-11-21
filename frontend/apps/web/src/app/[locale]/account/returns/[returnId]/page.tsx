import type { Metadata } from 'next';
import { getLocale, getTranslations } from 'next-intl/server';
import { ReturnDetailView } from '../../../../../components/returns/ReturnDetailView';

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations('Returns');
  return { title: t('detailMetaTitle'), robots: { index: false, follow: false } };
}

export default async function ReturnDetailPage({ params }: { params: Promise<{ returnId: string }> }) {
  const [{ returnId }, t, locale] = await Promise.all([params, getTranslations('Returns'), getLocale()]);
  return (
    <main id="main" className="mx-auto max-w-3xl px-4 py-6">
      <h1 className="mb-4 font-display text-2xl font-bold text-text md:text-3xl">{t('detailMetaTitle')}</h1>
      <ReturnDetailView locale={locale === 'bn' ? 'bn' : 'en'} returnId={decodeURIComponent(returnId)} />
    </main>
  );
}
