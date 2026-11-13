import type { Metadata } from 'next';
import { getLocale, getTranslations } from 'next-intl/server';
import { ConfirmationView } from '../../../../components/checkout/ConfirmationView';

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations('Checkout');
  return { title: t('confirmMetaTitle'), robots: { index: false, follow: false } };
}

// TODO(E3.6): the real confirmation and order page (payment choice is E3.5). This placeholder only shows the number and status.
export default async function ConfirmationPage() {
  const [t, locale] = await Promise.all([getTranslations('Checkout'), getLocale()]);
  return (
    <main id="main" className="mx-auto max-w-3xl px-4 py-6">
      <h1 className="mb-4 font-display text-2xl font-bold text-text md:text-3xl">{t('confirmTitle')}</h1>
      <ConfirmationView locale={locale === 'bn' ? 'bn' : 'en'} />
    </main>
  );
}
