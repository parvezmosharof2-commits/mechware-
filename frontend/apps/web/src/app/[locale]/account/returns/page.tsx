import type { Metadata } from 'next';
import { getLocale, getTranslations } from 'next-intl/server';
import { ReturnsListView } from '../../../../components/returns/ReturnsListView';

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations('Returns');
  return { title: t('metaTitle'), robots: { index: false, follow: false } };
}

export default async function ReturnsPage() {
  const [t, locale] = await Promise.all([getTranslations('Returns'), getLocale()]);
  return (
    <main id="main" className="mx-auto max-w-3xl px-4 py-6">
      <h1 className="mb-4 font-display text-2xl font-bold text-text md:text-3xl">{t('title')}</h1>
      <ReturnsListView locale={locale === 'bn' ? 'bn' : 'en'} />
    </main>
  );
}
