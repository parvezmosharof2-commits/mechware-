import Link from 'next/link';
import type { SearchHit } from '@mechware/api-client';
import { Availability, PartNumberText, hitPrice, hitTitle, type T } from '../search/hit-parts';

/** Server-rendered product cards for the home page, built from the same pieces as the search results. */
export function ProductStrip({ hits, locale, t }: { hits: SearchHit[]; locale: string; t: T }) {
  const prefix = locale === 'bn' ? '/bn' : '';
  return (
    <ul className="grid grid-cols-1 gap-3 min-[480px]:grid-cols-2 lg:grid-cols-4">
      {hits.map((h) => (
        <li key={h.id} className="flex flex-col rounded-lg border border-surface-border bg-bg-subtle p-4">
          <p className="text-xs font-semibold uppercase tracking-wide text-text-muted">{h.brandName}</p>
          <Link prefetch={false} href={`${prefix}/products/${h.slug}`} className="mt-1 line-clamp-2 font-semibold text-text hover:text-accent hover:underline">
            {hitTitle(h, locale)}
          </Link>
          <p className="mt-2"><PartNumberText hit={h} highlight={false} t={t} /></p>
          <div className="mt-auto flex items-end justify-between gap-2 pt-3">
            <span className="font-semibold text-text">{hitPrice(h, locale, t)}</span>
            <Availability hit={h} t={t} />
          </div>
        </li>
      ))}
    </ul>
  );
}
