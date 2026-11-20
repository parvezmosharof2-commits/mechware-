'use client';

import * as React from 'react';
import { useLocale, useTranslations } from 'next-intl';
import { type QuoteErrorKind, formatPriceMinor, quoteStatusKey, quoteStatusTone, rfqStatusKey, rfqStatusTone } from '@mechware/api-client';

export function useNumberFormat() {
  const locale = useLocale();
  const nf = React.useMemo(() => new Intl.NumberFormat(locale === 'bn' ? 'bn-BD' : 'en-US'), [locale]);
  return React.useCallback((n: number) => nf.format(n), [nf]);
}

/** A calendar day (YYYY-MM-DD) or an instant, shown as a Dhaka-time date. */
export function formatDay(value: string, locale: 'en' | 'bn'): string {
  const d = /^\d{4}-\d{2}-\d{2}$/.test(value) ? new Date(`${value}T00:00:00Z`) : new Date(value);
  if (Number.isNaN(d.getTime())) return value;
  return new Intl.DateTimeFormat(locale === 'bn' ? 'bn-BD' : 'en-GB', { dateStyle: 'medium', timeZone: /^\d{4}-\d{2}-\d{2}$/.test(value) ? 'UTC' : 'Asia/Dhaka' }).format(d);
}

const TONE: Record<string, string> = {
  info: 'border-accent/40 bg-accent/10 text-text',
  ok: 'border-status-success/50 bg-status-success/10 text-text',
  muted: 'border-surface-border-strong bg-bg-subtle text-text-muted',
  neutral: 'border-surface-border-strong bg-bg-muted text-text'
};

/** Status is words, never colour alone. A withdrawn request says so. */
export function QuoteStatusBadge({ status, withdrawn }: { status: string; withdrawn: boolean }) {
  const t = useTranslations('Quotes');
  const label = withdrawn ? t('status_withdrawn') : t(`status_${rfqStatusKey(status)}` as 'status_other');
  return <span className={`inline-flex rounded-full border px-2.5 py-0.5 text-xs font-semibold ${TONE[withdrawn ? 'muted' : rfqStatusTone(status)]}`}>{label}</span>;
}

const BUYER_TONE: Record<string, string> = { ...TONE, ok: 'border-status-online/40 bg-status-online/10 text-text', warn: 'border-status-pending/40 bg-status-pending/10 text-text' };

/** A quote's status in words (never colour alone). */
export function BuyerQuoteBadge({ status }: { status: string }) {
  const t = useTranslations('BuyerQuotes');
  return <span className={`inline-flex rounded-full border px-2.5 py-0.5 text-xs font-semibold ${BUYER_TONE[quoteStatusTone(status)]}`}>{t(`status_${quoteStatusKey(status)}` as 'status_other')}</span>;
}

export function useQuoteMoney() {
  const locale = useLocale() === 'bn' ? 'bn' : 'en';
  return (minor: number) => formatPriceMinor(minor, 'BDT', locale);
}

/** The words for a refusal (`BuyerQuotes.err_<kind>`); the server English text is never shown. */
export function useQuoteErrorText() {
  const t = useTranslations('BuyerQuotes');
  return (kind: QuoteErrorKind) => t(`err_${kind}` as 'err_invalid');
}
