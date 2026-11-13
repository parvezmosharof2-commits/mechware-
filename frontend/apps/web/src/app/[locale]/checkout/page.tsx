import type { Metadata } from 'next';
import { getLocale, getTranslations } from 'next-intl/server';
import { CheckoutView } from '../../../components/checkout/CheckoutView';

// A private, authenticated page: not for search engines. No JSON-LD.
export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations('Checkout');
  return { title: t('metaTitle'), robots: { index: false, follow: false } };
}

/** Server shell (heading and layout); the steps run in the browser, where the buyer's session and cart are. */
export default async function CheckoutPage() {
  const [t, locale] = await Promise.all([getTranslations('Checkout'), getLocale()]);
  return (
    <main id="main" className="mx-auto max-w-7xl px-4 py-6">
      <h1 className="mb-4 font-display text-2xl font-bold text-text md:text-3xl">{t('title')}</h1>
      <CheckoutView locale={locale === 'bn' ? 'bn' : 'en'} />
    </main>
  );
}
