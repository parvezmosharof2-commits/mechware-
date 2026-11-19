'use client';

import Link from 'next/link';
import { useLocale, useTranslations } from 'next-intl';
import { requestQuoteHref } from '@mechware/api-client/src/rfq-buyer-model';
import { useQuoteAccess } from '../../lib/useQuoteAccess';
import type { QuoteEntryProps } from './QuoteEntry';

/** Loaded lazily by `QuoteEntry`, only for a signed-in person: checks the company list, then shows the link or the short note. */
export default function QuoteEntryLink({ source, variantId, quantity, label, text, look = 'button', disabled, testId }: QuoteEntryProps) {
  const t = useTranslations('Quotes');
  const locale = useLocale();
  const access = useQuoteAccess();
  if (access.phase === 'loading' || access.phase === 'error') return null;
  if (access.phase !== 'ready') {
    return (
      <p data-testid="quote-note" className="text-xs text-text-muted">
        {t('companyOnlyNote')}
      </p>
    );
  }
  const href = `${locale === 'bn' ? '/bn' : ''}${requestQuoteHref(source, { variantId, quantity, label })}`;
  const cls =
    look === 'button'
      ? 'inline-flex h-11 items-center justify-center rounded-md border border-surface-border-strong bg-bg-muted px-5 text-sm font-semibold text-text hover:bg-surface focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent'
      : 'text-sm font-medium text-accent underline-offset-4 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent';
  if (disabled) {
    return (
      <span aria-disabled="true" className={`${cls} cursor-not-allowed opacity-50`} data-testid={testId}>
        {text}
      </span>
    );
  }
  return (
    <Link prefetch={false} href={href} className={cls} data-testid={testId}>
      {text}
    </Link>
  );
}
