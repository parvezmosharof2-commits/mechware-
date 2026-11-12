import * as React from 'react';
import { Badge } from '@mechware/ui/src/components/Badge';
import { formatPriceMinor, type SearchHit } from '@mechware/api-client';
import { matchesPartNumber } from '../../lib/search-ui';

export type T = (key: string, values?: Record<string, string | number>) => string;

export function hitTitle(h: SearchHit, locale: string): string {
  return locale === 'bn' && h.titleBn.trim() ? h.titleBn : h.titleEn;
}

export function hitPrice(h: SearchHit, locale: string, t: T): string {
  return h.priceMinor === 0 ? t('priceOnRequest') : formatPriceMinor(h.priceMinor, h.currency, locale);
}

/** The first part number (or the matched one when the query hit a later alias), "+N" for the rest. */
export function PartNumberText({ hit, query, highlight, t }: { hit: SearchHit; query?: string; highlight: boolean; t: T }) {
  const matched = highlight ? hit.partNumbers.find((p) => matchesPartNumber(query, p)) : undefined;
  const shown = matched ?? hit.partNumbers[0];
  if (!shown) return <span className="text-text-subtle">{t('noPartNumber')}</span>;
  const more = hit.partNumbers.length - 1;
  return (
    <>
      <span className="font-mono text-sm text-text">
        {matched ? <mark className="rounded-xs bg-accent/20 px-0.5 text-text">{shown}</mark> : shown}
      </span>
      {more > 0 && <span className="ml-1 text-xs text-text-muted">{t('morePartNumbers', { count: more })}</span>}
    </>
  );
}

/** Stock and supply-status badges; the product page passes the selected variant's stock in the same shape. */
export function Availability({ hit, t }: { hit: Pick<SearchHit, 'inStock' | 'supplyStatus'>; t: T }) {
  return (
    <span className="inline-flex flex-wrap gap-1">
      {hit.inStock ? (
        <Badge variant="success" size="sm">{t('inStock')}</Badge>
      ) : (
        <Badge variant="default" size="sm">{t('outOfStock')}</Badge>
      )}
      {hit.supplyStatus === 'nrnd' && <Badge variant="low-stock" size="sm">{t('nrnd')}</Badge>}
      {hit.supplyStatus === 'obsolete' && <Badge variant="deal" size="sm">{t('obsolete')}</Badge>}
    </span>
  );
}
