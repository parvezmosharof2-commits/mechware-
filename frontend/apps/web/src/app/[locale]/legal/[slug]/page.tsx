import type { Metadata } from 'next';
import { notFound } from 'next/navigation';
import { getLocale, getTranslations } from 'next-intl/server';
import { LEGAL_APPROVED, LEGAL_SLUGS, isLegalSlug, legalDoc } from '../../../../content/legal';

type Props = { params: Promise<{ locale: string; slug: string }> };

export function generateStaticParams() {
  return ['en', 'bn'].flatMap((locale) => LEGAL_SLUGS.map((slug) => ({ locale, slug })));
}

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const { locale, slug } = await params;
  if (!isLegalSlug(slug)) return {};
  const doc = legalDoc(slug, locale);
  const path = `/legal/${slug}`;
  return {
    title: `${doc.title} | MECHWARE`,
    description: doc.intro,
    // Draft text is kept out of search results until the lawyer approves it (flip LEGAL_APPROVED).
    robots: LEGAL_APPROVED ? undefined : { index: false },
    alternates: { canonical: locale === 'bn' ? `/bn${path}` : path, languages: { en: path, bn: `/bn${path}`, 'x-default': path } }
  };
}

function formatDate(iso: string, locale: string): string {
  return new Intl.DateTimeFormat(locale === 'bn' ? 'bn-BD' : 'en-GB', { dateStyle: 'long', timeZone: 'UTC' }).format(new Date(iso));
}

export default async function LegalPage({ params }: Props) {
  const { slug } = await params;
  if (!isLegalSlug(slug)) notFound();
  const [locale, t] = await Promise.all([getLocale(), getTranslations('Legal')]);
  const doc = legalDoc(slug, locale);

  return (
    <main id="main" className="legal-doc mx-auto max-w-3xl px-4 py-10">
      <h1 className="font-display text-3xl font-extrabold tracking-tight text-text md:text-4xl">{doc.title}</h1>
      {!LEGAL_APPROVED && (
        <p className="mt-4 rounded-lg border-2 border-accent bg-bg-subtle px-4 py-3 text-base font-semibold text-text" data-testid="draft-notice">
          {t('draftNotice')}
        </p>
      )}
      <p className="mt-3 text-sm text-text-muted">{t('lastUpdated', { date: formatDate(doc.updated, locale) })}</p>
      {doc.intro && <p className="mt-4 text-text-muted">{doc.intro}</p>}

      <nav aria-label={t('toc')} className="mt-6 rounded-lg border border-surface-border p-4 print:hidden">
        <h2 className="text-sm font-semibold text-text">{t('toc')}</h2>
        <ol className="mt-2 list-decimal space-y-1 ps-5 text-sm">
          {doc.sections.map((s, i) => (
            <li key={i}>
              <a href={`#s${i + 1}`} className="text-text-muted underline hover:text-text">{s.h}</a>
            </li>
          ))}
        </ol>
      </nav>

      {doc.sections.map((s, i) => (
        <section key={i} aria-labelledby={`s${i + 1}`} className="mt-8">
          <h2 id={`s${i + 1}`} className="scroll-mt-24 text-xl font-bold text-text">{s.h}</h2>
          {s.p?.map((para, j) => <p key={j} className="mt-3 leading-relaxed text-text-muted">{para}</p>)}
          {s.l && (
            <ul className="mt-3 list-disc space-y-1 ps-6 text-text-muted">
              {s.l.map((item, j) => <li key={j}>{item}</li>)}
            </ul>
          )}
          {s.after?.map((para, j) => <p key={j} className="mt-3 leading-relaxed text-text-muted">{para}</p>)}
        </section>
      ))}
    </main>
  );
}
