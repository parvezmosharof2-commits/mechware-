'use client';

import * as React from 'react';
import { useLocale, useTranslations } from 'next-intl';
import { AlertBanner } from '@mechware/ui/src/components/AlertBanner';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import { Progress } from '@mechware/ui/src/components/Progress';
import {
  CREDIT_PANEL_PAGE_SIZE,
  CREDIT_STATEMENT_PAGE_SIZE,
  buyerAgingRows,
  creditPageCount,
  creditStatusKey,
  creditStatusTone,
  creditUsedPercent,
  formatCreditDay,
  buyerPaymentMethodKey,
  creditSignOf,
  buyerStatementFilterProblem,
  buyerStatementKindKey,
  creditUsageTone,
  formatPriceMinor,
  type BuyerCredit,
  type BuyerOpenItem,
  type BuyerStatementLine,
  type CreditTone,
  type BuyerStatementFilterProblem
} from '@mechware/api-client';
import { buyerCreditApi } from '../../lib/api';
import { statusChipClass } from '../../lib/orders-ui';
import { useResource } from '../../lib/useResource';
import { ListSkeleton } from './shared';

function useFormat() {
  const locale = useLocale() === 'bn' ? 'bn' : 'en';
  return {
    money: (minor: number, currency = 'BDT') => formatPriceMinor(minor, currency, locale),
    day: (d: string | null | undefined) => formatCreditDay(d, locale)
  };
}

const TONE: Record<CreditTone, Parameters<typeof statusChipClass>[0]> = { neutral: 'neutral', pending: 'pending', done: 'done', problem: 'problem' };

/** Status as words (never colour alone). */
function StatusChip({ status, group }: { status: string; group: 'item' | 'order' }) {
  const t = useTranslations('CompanyCredit.status');
  return (
    <span className={`inline-flex items-center rounded-full border px-2.5 py-0.5 text-xs font-semibold ${statusChipClass(TONE[creditStatusTone(status)])}`}>
      {t(creditStatusKey(status, group))}
    </span>
  );
}

const sectionClass = 'rounded-lg border border-border-subtle bg-bg-surface p-4 sm:p-5';

function Figure({ label, value, testId }: { label: string; value: string; testId?: string }) {
  return (
    <div>
      <dt className="text-xs text-fg-muted">{label}</dt>
      <dd data-testid={testId} className="font-semibold tabular-nums">{value}</dd>
    </div>
  );
}

/** Balance, hold state, overdue ageing and the payments still waiting for confirmation. All figures come from the API as sent. */
export function BalanceSection({ credit }: { credit: BuyerCredit }) {
  const t = useTranslations('CompanyCredit');
  const tc = useTranslations('Company');
  const { money, day } = useFormat();
  const percent = creditUsedPercent(credit.limitMinor, credit.usedMinor);
  const terms = ['none', 'net15', 'net30'].includes(credit.terms) ? credit.terms : null;
  const m = (minor: number) => money(minor, credit.currency);
  return (
    <>
      {credit.onHold && (
        <div data-testid="credit-hold">
          <AlertBanner variant="warning" title={t('hold.title')}>
            {t('hold.body')}
          </AlertBanner>
        </div>
      )}
      <section aria-labelledby="credit-balance" className={sectionClass}>
        <div className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
          <h2 id="credit-balance" className="text-lg font-semibold">{t('balance.heading')}</h2>
          <p className="text-xs text-fg-muted">{t('balance.asOf', { date: day(credit.asOf) })}</p>
        </div>
        {terms && <p className="mt-1 text-sm text-fg-secondary">{t('balance.terms')}: {tc(`credit.terms.${terms}`)}</p>}
        <div className="mt-3">
          <Progress value={percent} aria-label={t('balance.usedBar')} variant={creditUsageTone(percent)} />
          <p className="mt-1 text-xs text-fg-muted">{t('balance.usedPercent', { percent })}</p>
        </div>
        <dl className="mt-4 grid grid-cols-1 gap-3 text-sm min-[420px]:grid-cols-3">
          <Figure label={t('balance.limit')} value={m(credit.limitMinor)} testId="credit-limit" />
          <Figure label={t('balance.used')} value={m(credit.usedMinor)} testId="credit-used" />
          <Figure label={t('balance.available')} value={m(credit.availableMinor)} testId="credit-available" />
        </dl>
        <dl className="mt-3 grid grid-cols-1 gap-3 border-t border-border-subtle pt-3 text-sm min-[420px]:grid-cols-3">
          <Figure label={t('balance.onAccount')} value={m(credit.onAccountMinor)} testId="credit-on-account" />
          <Figure label={t('balance.overdue')} value={m(credit.overdueMinor)} testId="credit-overdue" />
          {credit.oldestDueOn && <Figure label={t('balance.oldestDue')} value={day(credit.oldestDueOn)} />}
        </dl>
      </section>

      {credit.overdueMinor > 0 && (
        <section aria-labelledby="credit-aging" className={sectionClass}>
          <h2 id="credit-aging" className="text-lg font-semibold">{t('aging.heading')}</h2>
          <dl className="mt-3 grid grid-cols-1 gap-2 text-sm min-[420px]:grid-cols-2">
            {buyerAgingRows(credit.aging).map((row) => (
              <div key={row.key} className="flex items-baseline justify-between gap-3 border-b border-border-subtle pb-1">
                <dt className="text-fg-muted">{t(`aging.${row.key}`)}</dt>
                <dd className="font-semibold tabular-nums">{m(row.minor)}</dd>
              </div>
            ))}
          </dl>
        </section>
      )}

      {credit.pendingPayments.length > 0 && (
        <section aria-labelledby="credit-pending" className={sectionClass} data-testid="credit-pending">
          <h2 id="credit-pending" className="text-lg font-semibold">{t('pending.heading')}</h2>
          <p className="mt-1 text-sm text-fg-secondary">{t('pending.note')}</p>
          <ul className="mt-3 divide-y divide-border-subtle text-sm">
            {credit.pendingPayments.map((p, i) => (
              <li key={`${p.receivedOn}-${p.referenceTail}-${i}`} className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1 py-2">
                <span>
                  {t('pending.item', { method: t(`method.${buyerPaymentMethodKey(p.method)}`), date: day(p.receivedOn) })}
                  {p.referenceTail && <span className="ml-2 font-mono text-xs text-fg-muted">{t('pending.reference', { tail: p.referenceTail })}</span>}
                </span>
                <span className="font-semibold tabular-nums">{m(p.amountMinor)}</span>
              </li>
            ))}
          </ul>
        </section>
      )}
    </>
  );
}

function Pager({ page, pages, label, onGo }: { page: number; pages: number; label: string; onGo: (next: number) => void }) {
  const t = useTranslations('CompanyCredit.pager');
  if (pages <= 1) return null;
  return (
    <nav aria-label={label} className="mt-4 flex flex-wrap items-center justify-between gap-3">
      <Button variant="outline" size="sm" onClick={() => onGo(page - 1)} disabled={page <= 1}>{t('prev')}</Button>
      <span className="text-sm text-fg-muted">{t('pageOf', { page, total: pages })}</span>
      <Button variant="outline" size="sm" onClick={() => onGo(page + 1)} disabled={page >= pages}>{t('next')}</Button>
    </nav>
  );
}

function LoadFailed({ message, onRetry }: { message: string; onRetry: () => void }) {
  const t = useTranslations('CompanyCredit');
  return (
    <div role="alert" className="mt-3 space-y-3 rounded-lg border border-fault/50 bg-fault/10 p-4 text-sm">
      <p>{message}</p>
      <Button variant="outline" size="sm" onClick={onRetry}>{t('retry')}</Button>
    </div>
  );
}

function OpenItemCard({ item, currency }: { item: BuyerOpenItem; currency: string }) {
  const t = useTranslations('CompanyCredit.open');
  const { money, day } = useFormat();
  return (
    <li className="py-3" data-testid="open-item">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <span className="break-all font-mono font-semibold" data-testid="open-item-number">{item.orderNumber}</span>
        <StatusChip status={item.status} group="item" />
      </div>
      <p className="mt-1 text-sm text-fg-secondary">
        {t('due', { date: day(item.dueOn) })}
        {item.daysPastDue > 0 && <span className="ml-2 font-semibold text-fault">{t('daysPastDue', { count: item.daysPastDue })}</span>}
      </p>
      <dl className="mt-2 grid grid-cols-2 gap-3 text-sm">
        <Figure label={t('orderAmount')} value={money(item.chargeMinor, currency)} />
        <Figure label={t('stillOwed')} value={money(item.receivableMinor, currency)} />
      </dl>
    </li>
  );
}

/** Orders that still owe something, oldest due date first. */
export function OpenItemsSection({ companyId, currency }: { companyId: string; currency: string }) {
  const t = useTranslations('CompanyCredit');
  const [page, setPage] = React.useState(1);
  const items = useResource(() => buyerCreditApi.openItems(companyId, page, CREDIT_PANEL_PAGE_SIZE), `open:${companyId}:${page}`);
  // The page size the server answered with decides the page count (it may clamp what was asked).
  const pages = creditPageCount(items.data?.totalCount ?? 0, items.data?.pageSize || CREDIT_PANEL_PAGE_SIZE);
  return (
    <section aria-labelledby="credit-open" aria-busy={items.loading} className={sectionClass} data-testid="credit-open">
      <h2 id="credit-open" className="text-lg font-semibold">{t('open.heading')}</h2>
      {items.error ? (
        <LoadFailed message={t('loadError')} onRetry={items.reload} />
      ) : !items.data ? (
        <div className="mt-3"><ListSkeleton rows={2} /></div>
      ) : items.data.items.length === 0 ? (
        <p className="mt-3 text-sm text-fg-secondary" data-testid="open-empty">{t('open.empty')}</p>
      ) : (
        <>
          <ul aria-label={t('open.listLabel')} className="mt-2 divide-y divide-border-subtle">
            {items.data.items.map((i) => <OpenItemCard key={i.orderNumber} item={i} currency={currency} />)}
          </ul>
          <Pager page={page} pages={pages} label={t('open.pagesLabel')} onGo={setPage} />
        </>
      )}
    </section>
  );
}

function StatementCard({ line, currency }: { line: BuyerStatementLine; currency: string }) {
  const t = useTranslations('CompanyCredit.statement');
  const tk = useTranslations('CompanyCredit');
  const { money, day } = useFormat();
  return (
    <li className="py-3" data-testid="statement-line">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <span className="font-semibold">{tk(`kind.${buyerStatementKindKey(line.kind)}`)}</span>
        <span className="text-xs text-fg-muted">{day(line.occurredOn)}</span>
      </div>
      <p className="mt-1 break-all text-sm text-fg-secondary">
        {line.orderNumber && <span className="mr-3 font-mono">{t('forOrder', { number: line.orderNumber })}</span>}
        {line.paymentMethod && <span className="mr-3">{tk(`method.${buyerPaymentMethodKey(line.paymentMethod)}`)}</span>}
        {line.referenceTail && <span className="mr-3 font-mono">{tk('pending.reference', { tail: line.referenceTail })}</span>}
        {line.dueOn && <span>{t('dueOn', { date: day(line.dueOn) })}</span>}
      </p>
      <dl className="mt-2 grid grid-cols-2 gap-x-3 gap-y-2 text-sm">
        {line.receivableDeltaMinor !== 0 && <Figure label={t('owedChange')} value={`${creditSignOf(line.receivableDeltaMinor) === '+' ? '+' : ''}${money(line.receivableDeltaMinor, currency)}`} />}
        {line.onAccountDeltaMinor !== 0 && <Figure label={t('onAccountChange')} value={`${creditSignOf(line.onAccountDeltaMinor) === '+' ? '+' : ''}${money(line.onAccountDeltaMinor, currency)}`} />}
        <Figure label={t('owedAfter')} value={money(line.receivableAfterMinor, currency)} />
        <Figure label={t('onAccountAfter')} value={money(line.onAccountAfterMinor, currency)} />
      </dl>
    </li>
  );
}

/** Statement with an optional from/to day filter and paging. Dates are Dhaka days in year-month-day form (native date inputs). */
export function StatementSection({ companyId, currency }: { companyId: string; currency: string }) {
  const t = useTranslations('CompanyCredit.statement');
  const { money } = useFormat();
  const [draft, setDraft] = React.useState({ from: '', to: '' });
  const [applied, setApplied] = React.useState({ from: '', to: '', page: 1 });
  const [problem, setProblem] = React.useState<BuyerStatementFilterProblem | null>(null);
  const key = `st:${companyId}:${applied.from}:${applied.to}:${applied.page}`;
  const statement = useResource(
    () => buyerCreditApi.statement(companyId, { from: applied.from, to: applied.to, page: applied.page, pageSize: CREDIT_STATEMENT_PAGE_SIZE }),
    key
  );

  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    const p = buyerStatementFilterProblem(draft.from, draft.to);
    setProblem(p);
    if (!p) setApplied({ from: draft.from.trim(), to: draft.to.trim(), page: 1 });
  };
  const clear = () => {
    setDraft({ from: '', to: '' });
    setProblem(null);
    setApplied({ from: '', to: '', page: 1 });
  };
  const data = statement.data;
  const pages = creditPageCount(data?.lines.totalCount ?? 0, data?.lines.pageSize || CREDIT_STATEMENT_PAGE_SIZE);
  const fromError = problem === 'fromFormat' || problem === 'order' ? t(problem) : undefined;
  const toError = problem === 'toFormat' ? t(problem) : undefined;

  return (
    <section aria-labelledby="credit-statement" aria-busy={statement.loading} className={sectionClass} data-testid="credit-statement">
      <h2 id="credit-statement" className="text-lg font-semibold">{t('heading')}</h2>
      <form onSubmit={submit} noValidate aria-label={t('filterLabel')} className="mt-3 grid gap-3 sm:grid-cols-[1fr_1fr_auto] sm:items-start">
        <Input id="st-from" type="date" label={t('from')} value={draft.from} onChange={(e) => setDraft({ ...draft, from: e.target.value })} error={fromError} />
        <Input id="st-to" type="date" label={t('to')} value={draft.to} onChange={(e) => setDraft({ ...draft, to: e.target.value })} error={toError} />
        <div className="flex gap-2 sm:mt-6">
          <Button type="submit" size="sm">{t('apply')}</Button>
          <Button type="button" variant="outline" size="sm" onClick={clear}>{t('clear')}</Button>
        </div>
      </form>

      {statement.error ? (
        <LoadFailed message={t('error')} onRetry={statement.reload} />
      ) : !data ? (
        <div className="mt-3"><ListSkeleton rows={3} /></div>
      ) : (
        <>
          <dl className="mt-4 grid grid-cols-1 gap-3 border-t border-border-subtle pt-3 text-sm min-[420px]:grid-cols-2">
            <Figure label={t('currentOwed')} value={money(data.currentReceivableMinor, currency)} testId="statement-owed" />
            <Figure label={t('currentOnAccount')} value={money(data.currentOnAccountMinor, currency)} testId="statement-on-account" />
          </dl>
          {data.lines.items.length === 0 ? (
            <p className="mt-3 text-sm text-fg-secondary" data-testid="statement-empty">{t('empty')}</p>
          ) : (
            <>
              <ul aria-label={t('listLabel')} className="mt-2 divide-y divide-border-subtle">
                {data.lines.items.map((l) => <StatementCard key={l.seq} line={l} currency={currency} />)}
              </ul>
              <Pager page={applied.page} pages={pages} label={t('pagesLabel')} onGo={(p) => setApplied({ ...applied, page: p })} />
            </>
          )}
        </>
      )}
    </section>
  );
}
