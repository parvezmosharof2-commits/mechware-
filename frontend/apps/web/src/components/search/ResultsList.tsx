import * as React from 'react';
import Link from 'next/link';
import { CopyPartNumber } from './CopyPartNumber';
import { Availability, PartNumberText, hitPrice, hitTitle } from './hit-parts';
import type { ResultsProps } from './ResultsTable';

/** Mobile (< 768 px) results: one card per hit; the title link covers the whole card, the copy button sits above it. */
export function ResultsList({ hits, locale, query, highlight, t }: ResultsProps) {
  const prefix = locale === 'bn' ? '/bn' : '';
  return (
    <ul className="md:hidden space-y-3" aria-label={t('tableCaption')}>
      {hits.map((h) => (
        <li
          key={h.id}
          className="relative rounded-lg border border-surface-border bg-bg-muted/40 p-4 focus-within:ring-2 focus-within:ring-accent hover:border-accent"
        >
          <Link
            href={`${prefix}/products/${h.slug}`}
            className="font-medium text-text after:absolute after:inset-0 after:content-[''] focus-visible:outline-none"
          >
            {hitTitle(h, locale)}
          </Link>
          <p className="text-xs text-text-muted">{h.brandName}</p>
          <div className="mt-2 flex items-center gap-1">
            <span>
              <PartNumberText hit={h} query={query} highlight={highlight} t={t} />
            </span>
            {h.partNumbers[0] && (
              <CopyPartNumber value={h.partNumbers[0]} label={t('copyPartNumber', { part: h.partNumbers[0] })} copiedLabel={t('copied')} />
            )}
          </div>
          <div className="mt-2 flex flex-wrap items-center justify-between gap-2">
            <Availability hit={h} t={t} />
            <span className="font-semibold tabular-nums text-text">{hitPrice(h, locale, t)}</span>
          </div>
        </li>
      ))}
    </ul>
  );
}
