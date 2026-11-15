import type { Metadata } from 'next';
import { getLocale, getTranslations } from 'next-intl/server';
import { OrderDetailView } from '../../../../../components/orders/OrderDetailView';

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations('Orders');
  return { title: t('detailMetaTitle'), robots: { index: false, follow: false } };
}

export default async function OrderDetailPage({ params }: { params: Promise<{ orderNumber: string }> }) {
  const [{ orderNumber }, t, locale] = await Promise.all([params, getTranslations('Orders'), getLocale()]);
  return (
    <main id="main" className="mx-auto max-w-3xl px-4 py-6">
      <h1 className="mb-4 font-display text-2xl font-bold text-text md:text-3xl">{t('detailMetaTitle')}</h1>
      <OrderDetailView locale={locale === 'bn' ? 'bn' : 'en'} orderNumber={decodeURIComponent(orderNumber)} />
    </main>
  );
}
