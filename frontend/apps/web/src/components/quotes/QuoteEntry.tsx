'use client';

import dynamic from 'next/dynamic';
import Link from 'next/link';
import { useLocale, useTranslations } from 'next-intl';
import type { RfqSource } from '@mechware/api-client/src/rfq-buyer-model';
import { useAuth } from '../../lib/auth';

export interface QuoteEntryProps {
  source: RfqSource;
  variantId?: string;
  quantity?: number;
  label?: string;
  /** Visible text of the link; the note for guests is fixed. */
  text: string;
  /** Link looks like a primary button (PDP, cart) or a plain text link. */
  look?: 'button' | 'link';
  disabled?: boolean;
  testId?: string;
}

// Loaded only after hydration and only for a signed-in person's session check: nothing private is part of the server HTML.
const EntryLazy = dynamic(() => import('./QuoteEntryLink'), { ssr: false });

/** "Ask for a quote" entry point for the cart, the product page and quick order. Guests and personal accounts see a short note instead. */
export function QuoteEntry(props: QuoteEntryProps) {
  const { status } = useAuth();
  const t = useTranslations('Quotes');
  const locale = useLocale();
  // A guest never downloads the company code: they see the note straight away (static text, no request).
  if (status === 'anonymous') {
    return (
      <p data-testid="quote-note" className="text-xs text-text-muted">
        {t('companyOnlyNote')}{' '}
        <Link prefetch={false} href={`${locale === 'bn' ? '/bn' : ''}/auth/login`} className="font-medium text-accent underline-offset-4 hover:underline">
          {t('signIn')}
        </Link>
      </p>
    );
  }
  if (status === 'loading') return null;
  return <EntryLazy {...props} />;
}
