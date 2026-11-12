'use client';

import * as React from 'react';
import { usePathname } from 'next/navigation';

/**
 * en <-> bn on the same page. English is unprefixed, Bangla lives under /bn, but the link to English is written /en/...:
 * next-intl remembers the last locale in a cookie and would send a bare path back to Bangla; /en/... resets the cookie
 * and redirects to the unprefixed URL. The query string is carried over.
 */
export function LanguageSwitch({ locale, label }: { locale: 'en' | 'bn'; label: string }) {
  const pathname = usePathname() || '/';
  const [search, setSearch] = React.useState('');
  React.useEffect(() => setSearch(window.location.search), [pathname]);

  const bare = pathname.replace(/^\/(bn|en)(?=\/|$)/, '') || '/';
  const hrefFor = (target: 'en' | 'bn') => `${target === 'bn' ? `/bn${bare === '/' ? '' : bare}` : `/en${bare === '/' ? '' : bare}`}${search}`;
  const item = (target: 'en' | 'bn', text: string, name: string) =>
    locale === target ? (
      <span aria-current="true" className="rounded-sm bg-accent px-2 py-1 text-xs font-bold text-accent-fg">
        {text}
      </span>
    ) : (
      <a href={hrefFor(target)} hrefLang={target} lang={target} aria-label={name} className="rounded-sm px-2 py-1 text-xs font-semibold text-text-muted hover:text-text">
        {text}
      </a>
    );

  return (
    <div role="group" aria-label={label} className="flex items-center gap-0.5 rounded-md border border-surface-border bg-bg-subtle p-0.5">
      {item('en', 'EN', 'EN - English')}
      {item('bn', 'বাং', 'বাং - বাংলা')}
    </div>
  );
}
