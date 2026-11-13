import Link from 'next/link';
import { cookies, headers } from 'next/headers';
import { getLocale, getTranslations } from 'next-intl/server';
import { topCategories } from '../../lib/storefront-server';
import { LanguageSwitch } from './LanguageSwitch';
import { HeaderAccount } from './HeaderAccount';
import { HeaderCart } from './HeaderCart';

export function SearchForm({ locale, placeholder, label, button, id, large }: {
  locale: 'en' | 'bn';
  placeholder: string;
  label: string;
  button: string;
  id: string;
  large?: boolean;
}) {
  // A plain GET form: it works before (and without) JavaScript.
  return (
    <form action={locale === 'bn' ? '/bn/search' : '/search'} method="get" role="search" aria-label={label} className="flex w-full">
      <label htmlFor={id} className="sr-only">{label}</label>
      <input
        id={id}
        name="q"
        type="search"
        autoComplete="off"
        placeholder={placeholder}
        className={`min-w-0 flex-1 rounded-l-md border border-r-0 border-surface-border bg-bg-subtle px-3 text-text placeholder:text-text-muted focus:border-accent focus:outline-none focus-visible:ring-2 focus-visible:ring-accent ${large ? 'h-14 text-base' : 'h-10 text-sm'}`}
      />
      <button
        type="submit"
        className={`rounded-r-md bg-accent px-4 font-semibold text-accent-fg transition-colors hover:bg-accent-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-2 focus-visible:ring-offset-bg ${large ? 'h-14 px-6 text-base' : 'h-10 text-sm'}`}
      >
        {button}
      </button>
    </form>
  );
}

/** Server component: only the account menu is a client island. */
export async function SiteHeader() {
  const [locale, t, categories] = await Promise.all([getLocale(), getTranslations('Layout'), topCategories()]);
  const loc: 'en' | 'bn' = locale === 'bn' ? 'bn' : 'en';
  const prefix = loc === 'bn' ? '/bn' : '';
  const current = (await headers()).get('x-mw-url') ?? '/';
  // UX hint only (no credential): hides the "Sign In" placeholder for a visitor who is signed in until the menu loads.
  const signedIn = (await cookies()).has('mw_session');

  return (
    <header className="sticky top-0 z-30 border-b border-surface-border bg-bg/95 backdrop-blur">
      <a href="#main" className="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:z-50 focus:rounded-md focus:bg-accent focus:px-3 focus:py-2 focus:text-accent-fg">
        {t('skipToContent')}
      </a>
      <div className="mx-auto flex max-w-7xl flex-wrap items-center gap-x-4 gap-y-2 px-4 py-2">
        <Link prefetch={false} href={prefix || '/'} className="font-display text-xl font-extrabold tracking-tight text-text" aria-label={t('homeLabel')}>
          MECH<span className="text-accent">WARE</span>
        </Link>

        <div className="order-3 w-full md:order-none md:max-w-xl md:flex-1">
          <SearchForm locale={loc} id="header-search" placeholder={t('searchPlaceholder')} label={t('searchLabelHeader')} button={t('searchButton')} />
        </div>

        <div className="ml-auto flex items-center gap-2 md:ml-0">
          <details className="group relative">
            <summary className="flex h-10 cursor-pointer list-none items-center gap-1 rounded-md border border-surface-border bg-bg-subtle px-3 text-sm font-semibold text-text hover:bg-surface focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent [&::-webkit-details-marker]:hidden">
              {t('categories')}
              <span aria-hidden className="text-xs text-text-muted transition-transform group-open:rotate-180">▾</span>
            </summary>
            <nav aria-label={t('categories')} className="absolute right-0 z-40 mt-2 max-h-[70vh] w-72 overflow-y-auto rounded-md border border-surface-border bg-bg-subtle p-2 shadow-lg md:left-0 md:right-auto">
              <ul>
                {categories.map((c) => (
                  <li key={c.id}>
                    <Link prefetch={false} href={`${prefix}/search?category=${encodeURIComponent(c.slug)}`} className="block rounded-sm px-3 py-2 text-sm text-text hover:bg-surface">
                      {loc === 'bn' && c.nameBn.trim() ? c.nameBn : c.nameEn}
                    </Link>
                  </li>
                ))}
                {categories.length === 0 && <li className="px-3 py-2 text-sm text-text-muted">{t('categoriesUnavailable')}</li>}
              </ul>
            </nav>
          </details>
          <LanguageSwitch locale={loc} label={t('language')} current={current} />
          <HeaderCart locale={loc} />
          <HeaderAccount locale={loc} initialSignedIn={signedIn} />
        </div>
      </div>
    </header>
  );
}
