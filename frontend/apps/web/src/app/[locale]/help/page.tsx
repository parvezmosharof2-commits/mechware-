import type { Metadata } from 'next';
import { getLocale, getTranslations } from 'next-intl/server';
import { jsonLdString } from '../../../lib/json-ld';

type Faq = { q: string; a: string; soon?: boolean };
type Group = { id: string; title: string; items: Faq[] };

export async function generateMetadata(): Promise<Metadata> {
  const [t, locale] = await Promise.all([getTranslations('Help'), getLocale()]);
  return {
    title: t('metaTitle'),
    description: t('metaDescription'),
    alternates: { canonical: locale === 'bn' ? '/bn/help' : '/help', languages: { en: '/help', bn: '/bn/help', 'x-default': '/help' } }
  };
}

export default async function HelpPage() {
  const t = await getTranslations('Help');
  const groups = t.raw('groups') as Group[];
  const jsonLd = {
    '@context': 'https://schema.org',
    '@type': 'FAQPage',
    mainEntity: groups.flatMap((g) => g.items.map((i) => ({ '@type': 'Question', name: i.q, acceptedAnswer: { '@type': 'Answer', text: i.a } })))
  };

  return (
    <main id="main" className="mx-auto max-w-3xl px-4 py-10">
      <script type="application/ld+json" dangerouslySetInnerHTML={{ __html: jsonLdString(jsonLd) }} />
      <h1 className="font-display text-3xl font-extrabold tracking-tight text-text md:text-4xl">{t('title')}</h1>
      <p className="mt-3 text-text-muted">{t('intro')}</p>

      {groups.map((g) => (
        <section key={g.id} aria-labelledby={`faq-${g.id}`} className="mt-10">
          <h2 id={`faq-${g.id}`} className="text-xl font-bold text-text">{g.title}</h2>
          <div className="mt-3 divide-y divide-surface-border rounded-lg border border-surface-border">
            {g.items.map((item) => (
              <details key={item.q} className="group px-4 py-3">
                <summary className="cursor-pointer font-medium text-text">{item.q}</summary>
                <p className="mt-2 leading-relaxed text-text-muted">
                  {item.a}
                  {item.soon && <span className="ms-2 rounded-full border border-surface-border px-2 py-0.5 text-xs text-text">{t('comingSoon')}</span>}
                </p>
              </details>
            ))}
          </div>
        </section>
      ))}

      <section aria-labelledby="faq-contact" className="mt-10">
        <h2 id="faq-contact" className="text-xl font-bold text-text">{t('contactTitle')}</h2>
        <p className="mt-3 text-text-muted">{t('contactPlaceholder')}</p>
      </section>
    </main>
  );
}
