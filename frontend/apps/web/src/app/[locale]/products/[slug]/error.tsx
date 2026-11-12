'use client';

import { useTranslations } from 'next-intl';

export default function ProductError({ reset }: { error: Error & { digest?: string }; reset: () => void }) {
  const t = useTranslations('Product');
  return (
    <main id="main" className="mx-auto max-w-2xl px-4 py-16 text-center">
      <h1 className="font-display text-2xl font-bold text-text">{t('errorTitle')}</h1>
      <p role="alert" className="mt-3 text-text-muted">
        {t('errorBody')}
      </p>
      <button
        type="button"
        onClick={reset}
        className="mt-6 inline-flex h-11 items-center rounded-md bg-accent px-5 text-sm font-semibold text-white focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-2"
      >
        {t('retry')}
      </button>
    </main>
  );
}
