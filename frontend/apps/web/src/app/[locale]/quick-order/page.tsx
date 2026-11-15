import type { Metadata } from 'next';
import { getLocale, getTranslations } from 'next-intl/server';
import { QuickOrderView } from '../../../components/quick-order/QuickOrderView';

// A working page for a buyer's own list, not content for search engines. No JSON-LD.
export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations('QuickOrder');
  return { title: t('metaTitle'), description: t('intro'), robots: { index: false, follow: false } };
}

/** Server shell (heading, intro); the checking and the cart calls run in the browser island. */
export default async function QuickOrderPage() {
  const [t, locale] = await Promise.all([getTranslations('QuickOrder'), getLocale()]);
  return (
    <main id="main" className="mx-auto max-w-7xl px-4 py-6">
      <h1 className="mb-2 font-display text-2xl font-bold text-text md:text-3xl">{t('title')}</h1>
      <p className="mb-6 max-w-3xl text-sm text-text-muted">{t('intro')}</p>
      <QuickOrderView locale={locale === 'bn' ? 'bn' : 'en'} />
    </main>
  );
}
