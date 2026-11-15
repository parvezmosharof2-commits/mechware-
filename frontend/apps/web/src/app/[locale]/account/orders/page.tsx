import type { Metadata } from 'next';
import { getLocale, getTranslations } from 'next-intl/server';
import { OrdersListView } from '../../../../components/orders/OrdersListView';

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations('Orders');
  return { title: t('metaTitle'), robots: { index: false, follow: false } };
}

export default async function OrdersPage() {
  const [t, locale] = await Promise.all([getTranslations('Orders'), getLocale()]);
  return (
    <main id="main" className="mx-auto max-w-3xl px-4 py-6">
      <h1 className="mb-4 font-display text-2xl font-bold text-text md:text-3xl">{t('title')}</h1>
      <OrdersListView locale={locale === 'bn' ? 'bn' : 'en'} />
    </main>
  );
}
