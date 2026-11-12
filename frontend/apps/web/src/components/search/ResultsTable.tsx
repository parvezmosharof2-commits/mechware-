import * as React from 'react';
import Link from 'next/link';
import type { SearchHit } from '@mechware/api-client';
import { CopyPartNumber } from './CopyPartNumber';
import { Availability, PartNumberText, hitPrice, hitTitle, type T } from './hit-parts';

export interface ResultsProps {
  hits: SearchHit[];
  locale: string;
  query?: string;
  /** True when the server matched the query as a part number: the matching part number is marked. */
  highlight: boolean;
  t: T;
}

/** Desktop (>= 768 px) results table. Rows keep the server's order; no client sorting. */
export function ResultsTable({ hits, locale, query, highlight, t }: ResultsProps) {
  const prefix = locale === 'bn' ? '/bn' : '';
  const th = 'px-4 py-3 text-xs font-semibold uppercase tracking-wide text-text-muted';
  return (
    <div className="hidden md:block overflow-x-auto rounded-lg border border-surface-border">
      <table className="w-full border-collapse text-left text-sm">
        <caption className="sr-only">{t('tableCaption')}</caption>
        <thead className="bg-bg-muted">
          <tr>
            <th scope="col" className={th}>{t('colProduct')}</th>
            <th scope="col" className={th}>{t('colPartNumber')}</th>
            <th scope="col" className={th}>{t('colAvailability')}</th>
            <th scope="col" className={`${th} text-right`}>{t('colPrice')}</th>
            <th scope="col" className={th}><span className="sr-only">{t('colAction')}</span></th>
          </tr>
        </thead>
        <tbody className="divide-y divide-surface-border">
          {hits.map((h) => {
            const title = hitTitle(h, locale);
            const href = `${prefix}/products/${h.slug}`;
            return (
              <tr key={h.id} className="even:bg-bg-muted/40 hover:bg-bg-muted">
                <td className="px-4 py-3 align-top">
                  <Link href={href} className="rounded-sm font-medium text-text hover:text-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent">
                    {title}
                  </Link>
                  <p className="text-xs text-text-muted">{h.brandName}</p>
                </td>
                <td className="px-4 py-3 align-top">
                  <div className="flex items-center gap-1">
                    <span>
                      <PartNumberText hit={h} query={query} highlight={highlight} t={t} />
                    </span>
                    {h.partNumbers[0] && (
                      <CopyPartNumber value={h.partNumbers[0]} label={t('copyPartNumber', { part: h.partNumbers[0] })} copiedLabel={t('copied')} />
                    )}
                  </div>
                </td>
                <td className="px-4 py-3 align-top">
                  <Availability hit={h} t={t} />
                </td>
                <td className="px-4 py-3 align-top text-right font-semibold tabular-nums text-text">{hitPrice(h, locale, t)}</td>
                <td className="px-4 py-3 align-top text-right">
                  <Link
                    href={href}
                    aria-label={t('viewProduct', { title })}
                    className="inline-flex h-9 items-center rounded-md border border-surface-border-strong px-3 text-sm font-medium text-text hover:border-accent hover:text-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                  >
                    {t('view')}
                  </Link>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
