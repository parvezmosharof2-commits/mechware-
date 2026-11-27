import type { Metadata } from 'next';
import { getLocale, getTranslations } from 'next-intl/server';
import { NotificationsListView } from '../../../../components/notifications/NotificationsListView';

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations('Notifications');
  return { title: t('metaTitle'), robots: { index: false, follow: false } };
}

export default async function NotificationsPage() {
  const [t, locale] = await Promise.all([getTranslations('Notifications'), getLocale()]);
  return (
    <main id="main" className="mx-auto max-w-3xl px-4 py-6">
      <h1 className="mb-4 font-display text-2xl font-bold text-text md:text-3xl">{t('pageTitle')}</h1>
      <NotificationsListView locale={locale === 'bn' ? 'bn' : 'en'} />
    </main>
  );
}
