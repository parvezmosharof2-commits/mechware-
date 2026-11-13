import type { Metadata } from 'next';
import { getLocale, getTranslations } from 'next-intl/server';
import { CartPageView } from '../../../components/cart/CartPageView';

// A private, per-visitor page: not for search engines. No JSON-LD.
export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations('Cart');
  return { title: t('metaTitle'), description: t('subtotalNote'), robots: { index: false, follow: false } };
}

/** Server shell (heading and layout); the cart itself loads in the browser, where the guest-cart cookie lives. */
export default async function CartPage() {
  const [t, locale] = await Promise.all([getTranslations('Cart'), getLocale()]);
  return (
    <main id="main" className="mx-auto max-w-7xl px-4 py-6">
      <h1 className="mb-6 font-display text-2xl font-bold text-text md:text-3xl">{t('title')}</h1>
      <CartPageView locale={locale === 'bn' ? 'bn' : 'en'} />
    </main>
  );
}
