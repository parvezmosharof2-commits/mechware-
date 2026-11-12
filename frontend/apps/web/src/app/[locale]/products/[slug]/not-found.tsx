import Link from 'next/link';
import { getLocale, getTranslations } from 'next-intl/server';
import { localePath } from '../../../../lib/product-ui';

export default async function ProductNotFound() {
  const t = await getTranslations('Product');
  const locale = await getLocale();
  return (
    <main className="mx-auto max-w-2xl px-4 py-16 text-center">
      <h1 className="font-display text-2xl font-bold text-text">{t('notFoundTitle')}</h1>
      <p className="mt-3 text-text-muted">{t('notFoundBody')}</p>
      <Link
        href={localePath(locale, '/search')}
        className="mt-6 inline-flex h-11 items-center rounded-md bg-accent px-5 text-sm font-semibold text-white focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-2"
      >
        {t('searchProducts')}
      </Link>
    </main>
  );
}
