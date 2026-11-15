'use client';

import * as React from 'react';
import { useTranslations } from 'next-intl';
import {
  formatPriceMinor,
  issueMessageKey,
  lineNoticeKey,
  lineTone,
  maxAvailableFix,
  statusMessageKey,
  variantPartText,
  type QuickOrderIssue,
  type QuickOrderLine,
  type QuickOrderVariant
} from '@mechware/api-client';

type Translate = ReturnType<typeof useTranslations>;

export interface RowActions {
  locale: 'en' | 'bn';
  currency: string;
  fmt: (n: number) => string;
  disabled: boolean;
  qtyDraft: (line: QuickOrderLine) => string;
  onQty: (line: QuickOrderLine, text: string) => void;
  onFixPart: (line: QuickOrderLine, part: string, quantity?: number) => void;
  onRetype: (lineNumber: number, text: string) => void;
}

const fixBtn =
  'inline-flex min-h-11 items-center rounded-md border border-accent px-3 text-sm font-semibold text-accent hover:bg-accent/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:opacity-50';
const cell = 'text-xs text-text-muted md:sr-only';
const GRID = 'md:grid-cols-[9.5rem_minmax(0,1fr)_7.5rem_7rem_7rem_6.5rem]';

export function RowHeader({ t }: { t: Translate }) {
  return (
    <div aria-hidden="true" className={`hidden gap-x-4 rounded-t-lg border border-b-0 border-surface-border bg-bg-muted px-4 py-3 text-xs font-semibold uppercase tracking-wide text-text-muted md:grid ${GRID}`}>
      <span>{t('colStatus')}</span>
      <span>{t('colProduct')}</span>
      <span>{t('colQuantity')}</span>
      <span>{t('colUnitPrice')}</span>
      <span>{t('colTotal')}</span>
      <span>{t('colStock')}</span>
    </div>
  );
}

const glyph = { ok: '✓', warning: '!', error: '✕' } as const;
const glyphColour = { ok: 'text-status-online', warning: 'text-status-pending', error: 'text-fault' } as const;

const title = (v: QuickOrderVariant, locale: string) => (locale === 'bn' && v.titleBn.trim() ? v.titleBn : v.title);

function Product({ v, locale, label }: { v: QuickOrderVariant; locale: string; label: string }) {
  return (
    <>
      <p className="break-words font-semibold text-text">{title(v, locale)}</p>
      <p className="mt-1 text-xs text-text-muted">
        {label}: <span className="break-all font-mono text-text">{variantPartText(v)}</span>
      </p>
    </>
  );
}

/** One result line: status icon + words, the matched product, the fix controls, quantity, server prices and stock. */
export function ResultRow({ line, a, t, partLabel }: { line: QuickOrderLine; a: RowActions; t: Translate; partLabel: string }) {
  const tone = lineTone(line);
  const v = line.variant;
  const raw = line.part || line.raw;
  const maxFix = maxAvailableFix(line);
  const firstLine = line.lineNumbers[0] ?? 0;
  const lineLabel = line.lineNumbers.length > 1 ? line.lineNumbers.map((n) => a.fmt(n)).join(', ') : a.fmt(firstLine);
  const price = (minor: number | null) => (minor === null ? '–' : formatPriceMinor(minor, a.currency, a.locale));
  const border = { ok: 'border-l-status-online', warning: 'border-l-status-pending', error: 'border-l-fault' }[tone];
  return (
    <li data-status={line.status} className={`grid gap-x-4 gap-y-3 border-b border-l-4 border-b-surface-border px-4 py-4 last:border-b-0 md:items-start ${GRID} ${border}`}>
      <div>
        <p className="flex items-center gap-2 font-semibold text-text">
          <span aria-hidden="true" className={`text-lg leading-none ${glyphColour[tone]}`}>{glyph[tone]}</span>
          {t(statusMessageKey(line.status))}
        </p>
        <p className="mt-1 text-xs text-text-muted">{t('lineNumber', { n: lineLabel })}</p>
      </div>

      <div className="min-w-0 space-y-2">
        {v && line.status !== 'PART_AMBIGUOUS' && <Product v={v} locale={a.locale} label={partLabel} />}
        <p className="break-all text-xs text-text-muted">{t('youTyped', { raw: line.raw })}</p>

        {line.status === 'PART_NOT_FOUND' && (
          <>
            <p className="text-sm text-text">{line.suggestions.length > 0 ? t('notFoundHelp') : t('notFoundNoSuggestion')}</p>
            <div className="flex flex-wrap gap-2">
              {line.suggestions.map((s) => (
                <button key={s.id} type="button" disabled={a.disabled} className={fixBtn} aria-label={t('useSuggestionLabel', { raw, part: variantPartText(s) })} onClick={() => a.onFixPart(line, variantPartText(s))}>
                  {t('useSuggestion', { part: variantPartText(s) })}
                </button>
              ))}
            </div>
          </>
        )}

        {line.status === 'PART_AMBIGUOUS' && (
          <>
            <p className="text-sm text-text">{t('ambiguousHelp')}</p>
            <select
              aria-label={t('ambiguousLabel', { raw })}
              disabled={a.disabled}
              defaultValue=""
              onChange={(e) => e.target.value && a.onFixPart(line, e.target.value)}
              className="min-h-11 w-full max-w-full rounded-md border border-surface-border-strong bg-bg-muted px-2 text-sm text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
            >
              <option value="">{t('ambiguousChoose')}</option>
              {line.candidates.map((c) => (
                <option key={c.id} value={variantPartText(c)}>
                  {variantPartText(c)} {'·'} {title(c, a.locale)}
                </option>
              ))}
            </select>
          </>
        )}

        {line.status === 'PART_OBSOLETE' && (
          <>
            <p className="text-sm text-text">{t('obsoleteHelp')}</p>
            {line.successor && (
              <div className="rounded-md border border-surface-border bg-bg-muted p-3">
                <p className="text-sm font-semibold text-text">{t('obsoleteSuccessor', { part: variantPartText(line.successor.variant) })}</p>
                <p className="mt-1 break-words text-xs text-text-muted">
                  {title(line.successor.variant, a.locale)} {line.successor.isDropIn ? t('successorDropIn') : t('successorCheck')} {line.successor.note ?? ''}
                </p>
                <button type="button" disabled={a.disabled} className={`${fixBtn} mt-2`} onClick={() => a.onFixPart(line, variantPartText(line.successor!.variant))}>
                  {t('useSuccessor', { part: variantPartText(line.successor.variant) })}
                </button>
              </div>
            )}
          </>
        )}

        {line.status === 'OUT_OF_STOCK' && <p className="text-sm text-text">{t('outOfStockHelp')}</p>}

        {line.status === 'INSUFFICIENT_STOCK' && (
          <>
            <p className="text-sm text-text">{t('insufficientHelp', { available: a.fmt(line.stock?.available ?? 0), requested: a.fmt(line.requestedQuantity) })}</p>
            {maxFix !== null && v && (
              <button type="button" disabled={a.disabled} className={fixBtn} aria-label={t('useAvailableLabel', { raw, available: a.fmt(maxFix) })} onClick={() => a.onFixPart(line, line.part, maxFix)}>
                {t('useAvailable', { available: a.fmt(maxFix) })}
              </button>
            )}
          </>
        )}

        {line.status === 'QTY_EXCEEDS_LIMIT' && <p className="text-sm text-text">{t('qtyExceedsHelp')}</p>}

        {line.notices.map((n, i) => (
          <p key={`${n.kind}-${i}`} className="rounded-md border border-status-pending/50 bg-status-pending/10 px-3 py-2 text-sm text-text">
            <span className="sr-only">{t('noticeLabel')}: </span>
            {t(lineNoticeKey(n.kind), { was: a.fmt(n.was ?? 0), now: a.fmt(n.now ?? 0) })}
          </p>
        ))}
      </div>

      <div>
        <p className={cell}>{t('colQuantity')}</p>
        {line.valid && v ? (
          <input
            type="number"
            inputMode="numeric"
            min={1}
            step={1}
            value={a.qtyDraft(line)}
            aria-label={t('quantityOf', { part: variantPartText(v) })}
            onChange={(e) => a.onQty(line, e.target.value)}
            className="h-11 w-24 rounded-md border border-surface-border-strong bg-bg-muted px-2 text-center text-sm text-text outline-none focus-visible:ring-2 focus-visible:ring-accent"
          />
        ) : (
          <p className="text-sm text-text">{t('requestedQty', { n: a.fmt(line.requestedQuantity) })}</p>
        )}
      </div>

      <div>
        <p className={cell}>{t('colUnitPrice')}</p>
        <p className="text-sm text-text">{price(line.unitPriceMinor)}</p>
      </div>
      <div>
        <p className={cell}>{t('colTotal')}</p>
        <p className="font-semibold text-text">{price(line.lineTotalMinor)}</p>
      </div>
      <div>
        <p className={cell}>{t('colStock')}</p>
        {line.stock ? (
          <>
            <p className="text-sm text-text">{t(`stock_${line.stock.status}` as 'stock_in_stock')}</p>
            <p className="text-xs text-text-muted">{t('stockAvailable', { count: a.fmt(line.stock.available) })}</p>
          </>
        ) : (
          <p className="text-sm text-text-muted">{'–'}</p>
        )}
      </div>
    </li>
  );
}

/** A line the parser could not read: say why and let the buyer retype just that line. */
export function ErrorRow({ issue, rawText, a, t }: { issue: QuickOrderIssue; rawText: string; a: RowActions; t: Translate }) {
  const [value, setValue] = React.useState(rawText);
  const id = `qo-retype-${issue.lineNumber}`;
  return (
    <li data-status={issue.code} className={`grid gap-x-4 gap-y-3 border-b border-l-4 border-b-surface-border border-l-fault px-4 py-4 last:border-b-0 md:items-start ${GRID}`}>
      <div>
        <p className="flex items-center gap-2 font-semibold text-text">
          <span aria-hidden="true" className="text-lg leading-none text-fault">{glyph.error}</span>
          {t('status_unknown')}
        </p>
        <p className="mt-1 text-xs text-text-muted">{t('lineNumber', { n: a.fmt(issue.lineNumber) })}</p>
      </div>
      <div className="min-w-0 space-y-2 md:col-span-5">
        <p className="text-sm text-text">{t(issueMessageKey(issue.code))}</p>
        <form
          className="flex flex-wrap items-center gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            a.onRetype(issue.lineNumber, value);
          }}
        >
          <label htmlFor={id} className="sr-only">{t('retypeLabel', { n: a.fmt(issue.lineNumber) })}</label>
          <input id={id} value={value} onChange={(e) => setValue(e.target.value)} disabled={a.disabled} className="h-11 min-w-0 flex-1 rounded-md border border-surface-border-strong bg-bg-muted px-3 font-mono text-sm text-text outline-none focus-visible:ring-2 focus-visible:ring-accent" />
          <button type="submit" disabled={a.disabled} className={fixBtn}>{t('retypeApply')}</button>
        </form>
      </div>
    </li>
  );
}
