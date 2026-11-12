import * as React from 'react';
import Link from 'next/link';
import { useLocale, useTranslations } from 'next-intl';
import { Box, Download, FileText, ShieldCheck } from 'lucide-react';
import type { ProductDetail } from '@mechware/api-client';
import { sanitizeHtml, type SafeNode } from '../../lib/safe-html';
import { absoluteMediaUrl, formatFileSize, pickLocalized, productPath, specBoolean, specGroups } from '../../lib/product-ui';

// The sanitiser returns a node tree; it is rendered as React elements, so no raw HTML reaches the DOM.
function renderNodes(nodes: SafeNode[]): React.ReactNode[] {
  return nodes.map((n, i) => {
    if (typeof n === 'string') return n;
    const kids = renderNodes(n.children);
    switch (n.tag) {
      case 'br':
        return <br key={i} />;
      case 'p':
        return (
          <p key={i} className="mt-3 first:mt-0">
            {kids}
          </p>
        );
      case 'h3':
        return (
          <h3 key={i} className="mt-5 text-base font-semibold text-text first:mt-0">
            {kids}
          </h3>
        );
      case 'h4':
        return (
          <h4 key={i} className="mt-4 text-sm font-semibold text-text first:mt-0">
            {kids}
          </h4>
        );
      case 'ul':
        return (
          <ul key={i} className="mt-3 list-disc space-y-1 pl-5">
            {kids}
          </ul>
        );
      case 'ol':
        return (
          <ol key={i} className="mt-3 list-decimal space-y-1 pl-5">
            {kids}
          </ol>
        );
      case 'table':
        return (
          <div key={i} className="mt-3 overflow-x-auto">
            <table className="w-full border-collapse text-sm">{kids}</table>
          </div>
        );
      case 'th':
        return (
          <th key={i} className="border border-surface-border bg-bg-muted px-2 py-1 text-left font-medium">
            {kids}
          </th>
        );
      case 'td':
        return (
          <td key={i} className="border border-surface-border px-2 py-1">
            {kids}
          </td>
        );
      default: {
        const Tag = n.tag;
        return <Tag key={i}>{kids}</Tag>;
      }
    }
  });
}

/** Warning for not-recommended / obsolete parts, with the successor and alternatives as links. */
export function LifecycleBanner({ product }: { product: ProductDetail }) {
  const t = useTranslations('Product');
  const locale = useLocale();
  const status = product.supplyStatus;
  const alternatives = product.alternatives ?? [];
  const successor = product.successor;
  const obsolete = status === 'obsolete';
  const flagged = status === 'nrnd' || obsolete;
  if (!flagged && !successor) return null;
  return (
    <section
      aria-label={t(obsolete ? 'lifecycleObsoleteTitle' : 'lifecycleNrndTitle')}
      className={`mb-6 rounded-lg border-2 p-4 text-sm ${obsolete ? 'border-error bg-error/10' : 'border-warning bg-warning/10'}`}
      role={obsolete ? 'alert' : undefined}
    >
      {flagged && (
        <>
          <p className="text-base font-semibold text-text">{t(obsolete ? 'lifecycleObsoleteTitle' : 'lifecycleNrndTitle')}</p>
          <p className="mt-1 text-text-muted">{t(obsolete ? 'lifecycleObsoleteBody' : 'lifecycleNrndBody')}</p>
        </>
      )}
      {successor && (
        <p className="mt-2 text-text">
          {t('replacedBy')}{' '}
          <Link href={productPath(locale, successor.slug)} className="font-medium text-accent underline">
            {pickLocalized(locale, successor.titleEn, successor.titleBn)}
          </Link>
          {successor.isDropIn && <span className="ml-2 rounded-sm bg-bg-muted px-1.5 py-0.5 text-xs">{t('dropIn')}</span>}
          {successor.note && <span className="block text-text-muted">{successor.note}</span>}
        </p>
      )}
      {alternatives.length > 0 && (
        <div className="mt-2">
          <p className="font-medium text-text">{t('alternatives')}</p>
          <ul className="mt-1 list-disc pl-5">
            {alternatives.map((a) => (
              <li key={a.slug}>
                <Link href={productPath(locale, a.slug)} className="text-accent underline">
                  {pickLocalized(locale, a.titleEn, a.titleBn)}
                </Link>
                {a.brandName && <span className="text-text-muted"> {t('alternativeBrand', { brand: a.brandName })}</span>}
              </li>
            ))}
          </ul>
        </div>
      )}
    </section>
  );
}

const DOC_ICON: Record<string, typeof FileText> = { datasheet: FileText, cad: Box, certificate: ShieldCheck };
const DOC_LABEL: Record<string, 'docDatasheet' | 'docCad' | 'docCertificate'> = {
  datasheet: 'docDatasheet',
  cad: 'docCad',
  certificate: 'docCertificate'
};

/** Server Component: the static part of the product page (description, specs, documents) is never hydrated. The variant-dependent availability section is ProductView's. */
export function ProductSections({ product, mediaBase }: { product: ProductDetail; mediaBase: string }) {
  const t = useTranslations('Product');
  const groups = specGroups(product.specs);
  const documents = product.documents ?? [];
  const html = product.descriptionHtml;
  const description = sanitizeHtml(html ?? '');
  const highlights = product.highlights.filter((h) => h.trim());
  const heading = 'font-display text-xl font-bold text-text';

  const links: [string, string][] = [
    ['pdp-specs', t('specifications')],
    ['pdp-documents', t('documents')],
    ['pdp-availability', t('availability')]
  ];

  return (
    <>
      <nav aria-label={t('sectionsNav')} className="sticky top-0 z-10 -mx-4 overflow-x-auto border-b border-surface-border bg-bg px-4">
        <ul className="flex gap-4 whitespace-nowrap text-sm">
          {links.map(([id, label]) => (
            <li key={id}>
              <a href={`#${id}`} className="inline-block py-3 text-text-muted hover:text-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent">
                {label}
              </a>
            </li>
          ))}
        </ul>
      </nav>

      {(highlights.length > 0 || description.length > 0) && (
        <section aria-labelledby="pdp-description" className="mt-6 scroll-mt-14">
          <h2 id="pdp-description" className={heading}>
            {t('description')}
          </h2>
          {highlights.length > 0 && (
            <div className="mt-3">
              <h3 className="text-sm font-semibold text-text">{t('highlights')}</h3>
              <ul className="mt-2 list-disc space-y-1 pl-5 text-text-muted">
                {highlights.map((h) => (
                  <li key={h}>{h}</li>
                ))}
              </ul>
            </div>
          )}
          {description.length > 0 && <div className="mt-4 max-w-3xl text-text-muted">{renderNodes(description)}</div>}
        </section>
      )}

      <section id="pdp-specs" aria-labelledby="pdp-specs-h" className="mt-8 scroll-mt-14">
        <h2 id="pdp-specs-h" className={heading}>
          {t('specifications')}
        </h2>
        {groups.length === 0 ? (
          <p className="mt-3 text-sm text-text-muted">{t('specsEmpty')}</p>
        ) : (
          <div className="mt-3 space-y-5">
            {groups.map(([group, rows]) => (
              <table key={group} className="w-full border-collapse text-sm">
                <caption className="pb-1 text-left font-semibold text-text">{group}</caption>
                <thead className="sr-only">
                  <tr>
                    <th scope="col">{t('specParameter')}</th>
                    <th scope="col">{t('specValue')}</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map(([name, value]) => {
                    const bool = specBoolean(value);
                    return (
                      <tr key={name} className="border-t border-surface-border">
                        <th scope="row" className="w-2/5 py-2 pr-3 text-left align-top font-normal text-text-muted">
                          {name}
                        </th>
                        <td className="py-2 text-text">{bool === undefined ? value : bool ? t('yes') : t('no')}</td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            ))}
          </div>
        )}
      </section>

      <section id="pdp-documents" aria-labelledby="pdp-documents-h" className="mt-8 scroll-mt-14">
        <h2 id="pdp-documents-h" className={heading}>
          {t('documents')}
        </h2>
        {documents.length === 0 ? (
          <p className="mt-3 text-sm text-text-muted">{t('documentsEmpty')}</p>
        ) : (
          <ul className="mt-3 divide-y divide-surface-border rounded-lg border border-surface-border">
            {documents.map((d) => {
              const kind = d.kind.toLowerCase();
              const Icon = DOC_ICON[kind] ?? FileText;
              const labelKey = DOC_LABEL[kind];
              const size = formatFileSize(d.sizeBytes);
              return (
                <li key={d.id}>
                  <a
                    href={absoluteMediaUrl(d.url, mediaBase)}
                    download={d.fileName}
                    rel="noopener"
                    aria-label={t('download', { name: d.fileName })}
                    className="flex items-center gap-3 p-3 hover:bg-bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                  >
                    <Icon className="h-5 w-5 shrink-0 text-accent" aria-hidden="true" />
                    <span className="min-w-0 flex-1">
                      <span className="block text-xs text-text-muted">{t(labelKey ?? 'docOther')}</span>
                      <span className="block truncate text-sm text-text">{d.fileName}</span>
                    </span>
                    {size && <span className="shrink-0 text-xs text-text-muted">{size}</span>}
                    <Download className="h-4 w-4 shrink-0 text-text-muted" aria-hidden="true" />
                  </a>
                </li>
              );
            })}
          </ul>
        )}
      </section>
    </>
  );
}
