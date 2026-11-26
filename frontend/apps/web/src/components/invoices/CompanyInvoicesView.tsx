'use client';

import * as React from 'react';
import Link from 'next/link';
import { usePathname, useRouter, useSearchParams } from 'next/navigation';
import { useLocale, useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import {
  INVOICE_PAGE_SIZE,
  invoiceFilterProblem,
  invoicePageCount,
  invoiceUrlQuery,
  parseInvoiceUrl,
  type InvoiceFilterProblem
} from '@mechware/api-client';
import { useAuth } from '../../lib/auth';
import { invoicesApi } from '../../lib/invoices-client';
import { useResource } from '../../lib/useResource';
import { ListSkeleton, PageShell, RequireSignIn, useLocalePath } from '../company/shared';
import { InvoiceFacts, RefusalText, refusalOf } from './InvoiceParts';

/** The 404 of these routes is one body for a stranger, a role that may not see invoices, and a wrong id: say "not available to you". */
export function NotAvailable({ companyId }: { companyId: string }) {
  const t = useTranslations('Invoices');
  const toPath = useLocalePath();
  return (
    <div data-testid="company-invoices-not-available" className="space-y-3 rounded-lg border border-surface-border p-5">
      <h2 className="text-lg font-semibold">{t('notAvailableTitle')}</h2>
      <p className="text-sm text-text-muted">{t('notAvailableBody')}</p>
      <Link href={toPath(`/account/company/${encodeURIComponent(companyId)}`)} className="text-sm font-semibold text-accent hover:underline">{t('backToCompany')}</Link>
    </div>
  );
}

function Pager({ page, pages, onGo }: { page: number; pages: number; onGo: (p: number) => void }) {
  const t = useTranslations('Invoices');
  if (pages <= 1) return null;
  return (
    <nav aria-label={t('pagesLabel')} className="mt-4 flex flex-wrap items-center justify-between gap-3">
      <Button variant="outline" size="sm" onClick={() => onGo(page - 1)} disabled={page <= 1}>{t('prev')}</Button>
      <span className="text-sm text-text-muted">{t('pageOf', { page, total: pages })}</span>
      <Button variant="outline" size="sm" onClick={() => onGo(page + 1)} disabled={page >= pages}>{t('next')}</Button>
    </nav>
  );
}

function List({ companyId }: { companyId: string }) {
  const t = useTranslations('Invoices');
  const locale = useLocale() === 'bn' ? 'bn' : 'en';
  const toPath = useLocalePath();
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();
  const { user } = useAuth();
  const applied = parseInvoiceUrl((k) => params.get(k));
  const [draft, setDraft] = React.useState({ from: applied.from, to: applied.to });
  const [problem, setProblem] = React.useState<InvoiceFilterProblem | null>(null);
  const go = (next: { from: string; to: string; page: number }) => {
    const q = invoiceUrlQuery(next);
    router.replace(q ? `${pathname}?${q}` : pathname, { scroll: false });
  };
  const res = useResource(
    () => invoicesApi.companyList(companyId, { from: applied.from, to: applied.to, page: applied.page, pageSize: INVOICE_PAGE_SIZE }),
    `${user?.id ?? ''}:${companyId}:${applied.from}:${applied.to}:${applied.page}`
  );
  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    const p = invoiceFilterProblem(draft.from, draft.to);
    setProblem(p);
    if (!p) go({ from: draft.from.trim(), to: draft.to.trim(), page: 1 });
  };
  const clear = () => {
    setDraft({ from: '', to: '' });
    setProblem(null);
    go({ from: '', to: '', page: 1 });
  };
  if (res.error && refusalOf(res.error) === 'notFound') return <NotAvailable companyId={companyId} />;
  const pages = invoicePageCount(res.data?.totalCount ?? 0, res.data?.pageSize || INVOICE_PAGE_SIZE);
  const fromError = problem === 'fromFormat' || problem === 'order' ? t(`filter.${problem}`) : undefined;
  const toError = problem === 'toFormat' ? t('filter.toFormat') : undefined;
  return (
    <section aria-labelledby="company-invoices" aria-busy={res.loading} data-testid="company-invoices" className="space-y-4">
      <h2 id="company-invoices" className="sr-only">{t('companyTitle')}</h2>
      <form onSubmit={submit} noValidate aria-label={t('filter.label')} className="grid gap-3 sm:grid-cols-[1fr_1fr_auto] sm:items-start">
        <Input id="inv-from" type="date" label={t('filter.from')} value={draft.from} onChange={(e) => setDraft({ ...draft, from: e.target.value })} error={fromError} />
        <Input id="inv-to" type="date" label={t('filter.to')} value={draft.to} onChange={(e) => setDraft({ ...draft, to: e.target.value })} error={toError} />
        <div className="flex gap-2 sm:mt-6">
          <Button type="submit" size="sm">{t('filter.apply')}</Button>
          <Button type="button" variant="outline" size="sm" onClick={clear}>{t('filter.clear')}</Button>
        </div>
      </form>
      {res.error ? (
        <div className="space-y-3">
          <RefusalText refusal={refusalOf(res.error)} />
          <Button variant="outline" size="sm" onClick={res.reload}>{t('retry')}</Button>
        </div>
      ) : !res.data ? (
        <ListSkeleton rows={3} />
      ) : res.data.items.length === 0 ? (
        <p data-testid="company-invoices-empty" className="text-sm text-text-muted">{t('companyEmpty')}</p>
      ) : (
        <>
          <p className="text-sm text-text-muted" data-testid="company-invoices-count">{t('count', { count: res.data.totalCount })}</p>
          <ul aria-label={t('companyListLabel')} className="divide-y divide-surface-border rounded-lg border border-surface-border px-4">
            {res.data.items.map((inv) => (
              <li key={inv.id} data-testid="company-invoice" className="py-3">
                <InvoiceFacts
                  invoice={inv}
                  locale={locale}
                  numberNode={
                    <Link prefetch={false} data-testid="invoice-open" href={toPath(`/account/company/${encodeURIComponent(companyId)}/invoices/${encodeURIComponent(inv.id)}`)} className="break-all font-mono font-semibold text-accent hover:underline">{inv.number}</Link>
                  }
                />
                <p className="mt-1 break-all text-sm text-text-muted">{t('forOrder', { number: inv.orderNumber })}</p>
              </li>
            ))}
          </ul>
          <Pager page={applied.page} pages={pages} onGo={(p) => go({ from: applied.from, to: applied.to, page: p })} />
        </>
      )}
    </section>
  );
}

/** "Company invoices": newest first, Dhaka-day filters kept in the URL, paging. Only roles the API allows get an answer; all others see "not available". */
export function CompanyInvoicesView({ companyId }: { companyId: string }) {
  const t = useTranslations('Invoices');
  const toPath = useLocalePath();
  return (
    <PageShell title={t('companyTitle')} back={{ href: toPath(`/account/company/${encodeURIComponent(companyId)}`), label: t('backToCompany') }}>
      <RequireSignIn>
        <List companyId={companyId} />
      </RequireSignIn>
    </PageShell>
  );
}
