import Link from 'next/link';
import { getLocale, getTranslations } from 'next-intl/server';

const LEGAL = [
  ['terms', '/legal/terms'],
  ['privacy', '/legal/privacy'],
  ['cookies', '/legal/cookies'],
  ['returns', '/legal/returns'],
  ['shipping', '/legal/shipping'],
  ['credit', '/legal/credit'],
  ['quoteTerms', '/legal/quote-terms']
] as const;

export async function SiteFooter() {
  const [locale, t] = await Promise.all([getLocale(), getTranslations('Layout')]);
  const prefix = locale === 'bn' ? '/bn' : '';
  const link = 'text-sm text-text-muted hover:text-text hover:underline';

  return (
    <footer className="mt-16 border-t border-surface-border bg-bg-subtle">
      <div className="mx-auto grid max-w-7xl gap-8 px-4 py-10 sm:grid-cols-2 lg:grid-cols-4">
        <div>
          <p className="font-display text-lg font-extrabold text-text">MECH<span className="text-accent">WARE</span></p>
          <p className="mt-2 text-sm text-text-muted">{t('footerTagline')}</p>
        </div>
        <nav aria-label={t('legalNav')}>
          <h2 className="text-sm font-semibold text-text">{t('legalHeading')}</h2>
          <ul className="mt-3 space-y-2">
            {LEGAL.map(([key, href]) => (
              <li key={key}><Link prefetch={false} href={`${prefix}${href}`} className={link}>{t(`legal.${key}`)}</Link></li>
            ))}
          </ul>
        </nav>
        <nav aria-label={t('helpNav')}>
          <h2 className="text-sm font-semibold text-text">{t('helpHeading')}</h2>
          <ul className="mt-3 space-y-2">
            <li><Link prefetch={false} href={`${prefix}/help`} className={link}>{t('helpCentre')}</Link></li>
            <li><Link prefetch={false} href={`${prefix}/search`} className={link}>{t('browseParts')}</Link></li>
          </ul>
        </nav>
        <div>
          <h2 className="text-sm font-semibold text-text">{t('contactHeading')}</h2>
          <p className="mt-3 text-sm text-text-muted">{t('contactPlaceholder')}</p>
        </div>
      </div>
      <div className="border-t border-surface-border px-4 py-4 text-center text-xs text-text-muted">
        {t('copyright', { year: new Date().getFullYear() })}
      </div>
    </footer>
  );
}
