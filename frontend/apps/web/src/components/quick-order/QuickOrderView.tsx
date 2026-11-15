'use client';

import * as React from 'react';
import Link from 'next/link';
import { useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import {
  QUICK_ORDER_MAX_INPUT,
  buildCartBatch,
  classifyCartError,
  classifyQuickOrderError,
  fixPart,
  fixQuantity,
  formatPriceMinor,
  groupLines,
  removeLines,
  rewriteLines,
  summarize,
  variantPartText,
  type AddOutcome,
  type CartBatchItem,
  type QuickOrderErrorKind,
  type QuickOrderLine,
  type QuickOrderResult
} from '@mechware/api-client';
import { useAuth } from '../../lib/auth';
import { announceCart } from '../../lib/cart-events';
import { ErrorRow, ResultRow, RowHeader, type RowActions } from './QuickOrderRows';

/** Wait after the last keystroke in a quantity box before the server checks again. */
const DEBOUNCE_MS = 600;
const MAX_FILE_BYTES = 100 * 1024;
const EXAMPLES = ['6ES7 214-1AG40-0XB0 5', '10 x LC1D09'];

type Fail = Extract<AddOutcome, { ok: false }>;

/** Paste or upload a list, check it against the catalogue, fix lines, add the valid ones to the cart. */
export function QuickOrderView({ locale }: { locale: 'en' | 'bn' }) {
  const t = useTranslations('QuickOrder');
  const { status: authStatus } = useAuth();
  const prefix = locale === 'bn' ? '/bn' : '';
  const nf = React.useMemo(() => new Intl.NumberFormat(locale === 'bn' ? 'bn-BD' : 'en-US'), [locale]);
  const fmt = React.useCallback((n: number) => nf.format(n), [nf]);

  const [text, setText] = React.useState('');
  const [checked, setChecked] = React.useState<{ result: QuickOrderResult; text: string } | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [error, setError] = React.useState<QuickOrderErrorKind | 'empty' | 'fileLarge' | 'fileWrong' | 'fileRead' | null>(null);
  const [drafts, setDrafts] = React.useState<Record<number, string>>({});
  const [adding, setAdding] = React.useState<{ done: number; total: number } | null>(null);
  const [report, setReport] = React.useState<AddOutcome[] | null>(null);
  const [live, setLive] = React.useState('');

  const textRef = React.useRef('');
  const inflight = React.useRef(false);
  const queued = React.useRef<{ value: string; focus: boolean } | null>(null);
  const focusNext = React.useRef<'result' | 'report' | null>(null);
  const timers = React.useRef(new Map<number, number>());
  const mounted = React.useRef(true);
  const resultRef = React.useRef<HTMLHeadingElement>(null);
  const reportRef = React.useRef<HTMLHeadingElement>(null);
  const areaRef = React.useRef<HTMLTextAreaElement>(null);

  React.useEffect(() => {
    mounted.current = true;
    const pending = timers.current;
    return () => {
      mounted.current = false;
      pending.forEach((id) => window.clearTimeout(id));
    };
  }, []);

  React.useEffect(() => {
    if (focusNext.current === 'report') reportRef.current?.focus();
    else if (focusNext.current === 'result') resultRef.current?.focus();
    focusNext.current = null;
  }, [checked, report]);

  const changeText = (value: string) => {
    textRef.current = value;
    setText(value);
  };

  /** One request at a time; a newer request waits and replaces older queued ones. The text is never touched by a failure. */
  const validate = React.useCallback(
    async (value: string, focus: boolean) => {
      if (value.trim() === '') {
        setError('empty');
        return;
      }
      if (value.length > QUICK_ORDER_MAX_INPUT) {
        setError('tooLarge');
        return;
      }
      if (inflight.current) {
        queued.current = { value, focus };
        return;
      }
      inflight.current = true;
      setBusy(true);
      setError(null);
      const { quickOrderApi } = await import('../../lib/quick-order-client');
      let job: { value: string; focus: boolean } | null = { value, focus };
      while (job) {
        try {
          const result = await quickOrderApi.validate(job.value);
          if (!mounted.current) return;
          const s = summarize(result);
          if (job.focus) focusNext.current = 'result';
          setChecked({ result, text: job.value });
          setDrafts({});
          setLive(result.lines.length + result.errors.length === 0 ? t('noLinesFound') : t('liveSummary', { valid: fmt(s.valid), attention: fmt(s.needsAttention) }));
        } catch (e) {
          if (!mounted.current) return;
          setError(classifyQuickOrderError(e));
        }
        job = queued.current;
        queued.current = null;
      }
      inflight.current = false;
      if (mounted.current) setBusy(false);
    },
    [t, fmt]
  );

  const applyText = (value: string, focus = true) => {
    changeText(value);
    void validate(value, focus);
  };

  const onFile = async (file: File | undefined) => {
    if (!file) return;
    if (!/\.(csv|txt)$/i.test(file.name) && !/^text\//.test(file.type)) return setError('fileWrong');
    if (file.size > MAX_FILE_BYTES) return setError('fileLarge');
    try {
      const content = await file.text();
      setError(null);
      changeText(content);
      areaRef.current?.focus();
    } catch {
      setError('fileRead');
    }
  };

  const groups = React.useMemo(() => (checked ? groupLines(checked.result) : null), [checked]);
  const stale = checked !== null && checked.text !== text;
  const cartBatch = React.useMemo(() => (groups ? buildCartBatch(groups.valid) : []), [groups]);
  const currency = checked?.result.summary.currency ?? 'BDT';

  const commitQty = (line: QuickOrderLine, value: string) => {
    const n = Number(value);
    const key = line.lineNumbers[0] ?? 0;
    timers.current.delete(key);
    if (!Number.isInteger(n) || n < 1 || n === line.quantity) {
      setDrafts((d) => {
        const rest = { ...d };
        delete rest[key];
        return rest;
      });
      return;
    }
    applyText(fixQuantity(textRef.current, line, n), false);
  };

  const onQty = (line: QuickOrderLine, value: string) => {
    const key = line.lineNumbers[0] ?? 0;
    setDrafts((d) => ({ ...d, [key]: value }));
    const old = timers.current.get(key);
    if (old !== undefined) window.clearTimeout(old);
    timers.current.set(key, window.setTimeout(() => commitQty(line, value), DEBOUNCE_MS));
  };

  const actions: RowActions = {
    locale,
    currency,
    fmt,
    disabled: busy || adding !== null,
    qtyDraft: (line) => drafts[line.lineNumbers[0] ?? 0] ?? String(line.quantity ?? line.requestedQuantity),
    onQty,
    onFixPart: (line, part, quantity) => applyText(fixPart(textRef.current, line, part, quantity)),
    onRetype: (lineNumber, value) => applyText(rewriteLines(textRef.current, [lineNumber], value.trim() === '' ? '' : value))
  };

  const addAll = async (items: CartBatchItem[]) => {
    if (items.length === 0 || adding) return;
    setAdding({ done: 0, total: items.length });
    const { cartApi } = await import('../../lib/cart-client');
    const outcomes: AddOutcome[] = [];
    let stop: string | null = null;
    for (const item of items) {
      if (stop) {
        outcomes.push({ item, ok: false, reason: stop });
      } else {
        try {
          announceCart((await cartApi.addItem(item.variantId, item.quantity)).lineCount);
          outcomes.push({ item, ok: true });
        } catch (e) {
          const reason = classifyCartError(e);
          // A 429 means every further call would be refused too: report the rest as not added instead of hammering.
          if (reason === 'rateLimited') stop = reason;
          outcomes.push({ item, ok: false, reason });
        }
      }
      if (mounted.current) setAdding({ done: outcomes.length, total: items.length });
    }
    if (!mounted.current) return;
    setAdding(null);
    const previous = (report ?? []).filter((o) => o.ok);
    focusNext.current = 'report';
    setReport([...previous, ...outcomes]);
    // Lines now in the cart leave the list; what is left (failed or needing attention) is checked again.
    const rest = removeLines(textRef.current, outcomes.filter((o) => o.ok).flatMap((o) => o.item.lineNumbers));
    if (rest.trim() === '') {
      changeText('');
      setChecked(null);
      setLive(t('addedStatus', { added: fmt(outcomes.filter((o) => o.ok).length), failed: fmt(outcomes.filter((o) => !o.ok).length) }));
    } else {
      applyText(rest, false);
    }
  };

  const failed = (report ?? []).filter((o): o is Fail => !o.ok);
  const added = (report ?? []).filter((o) => o.ok);
  const sum = checked ? summarize(checked.result) : null;
  const lineText = (n: number) => text.split(/\r\n|\n|\r/)[n - 1] ?? '';
  const errorText = error === 'empty' ? t('emptyInput') : error === 'fileLarge' ? t('fileTooLarge') : error === 'fileWrong' ? t('fileWrongType') : error === 'fileRead' ? t('fileUnreadable') : error ? t(`error_${error}`) : null;
  const canRetry = error !== null && !['empty', 'fileLarge', 'fileWrong', 'fileRead', 'tooLarge', 'tooManyLines', 'invalid'].includes(error);
  const addDisabled = busy || stale || adding !== null || cartBatch.length === 0 || authStatus === 'loading';

  return (
    <div className="space-y-6">
      <p role="status" className="sr-only">{busy ? t('loadingStatus') : live}</p>

      <section aria-label={t('title')} className="space-y-3">
        <label htmlFor="qo-text" className="block text-sm font-semibold text-text">{t('textareaLabel')}</label>
        <textarea
          id="qo-text"
          ref={areaRef}
          value={text}
          rows={8}
          spellCheck={false}
          autoComplete="off"
          readOnly={adding !== null}
          placeholder={t('placeholder')}
          onChange={(e) => changeText(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
              e.preventDefault();
              void validate(textRef.current, true);
            }
          }}
          className="w-full rounded-md border border-surface-border-strong bg-bg-muted p-3 font-mono text-sm text-text placeholder:text-text-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
        />
        <p className="text-xs text-text-muted">
          {t('examplesLabel')} {EXAMPLES.map((x) => <code key={x} className="mr-2 rounded bg-bg-muted px-1.5 py-0.5 font-mono text-text">{x}</code>)} {t('shortcutHint')}
        </p>
        <div className="flex flex-wrap items-center gap-3">
          <Button type="button" variant="accent" size="lg" disabled={busy || adding !== null} onClick={() => void validate(textRef.current, true)}>
            {busy ? t('checking') : t('check')}
          </Button>
          <label className="inline-flex min-h-11 cursor-pointer items-center rounded-md border border-surface-border-strong bg-bg-muted px-4 text-sm font-semibold text-text focus-within:ring-2 focus-within:ring-accent hover:bg-surface">
            {t('fileButton')}
            <input
              type="file"
              accept=".csv,.txt,text/csv,text/plain"
              className="sr-only"
              onChange={(e) => {
                void onFile(e.target.files?.[0]);
                e.target.value = '';
              }}
            />
          </label>
          {text !== '' && (
            <Button type="button" variant="ghost" size="lg" disabled={adding !== null} onClick={() => { changeText(''); setChecked(null); setError(null); setReport(null); areaRef.current?.focus(); }}>
              {t('clear')}
            </Button>
          )}
        </div>
        <p className="text-xs text-text-muted">{t('fileHint')}</p>
        {errorText && (
          <div role="alert" className="flex flex-wrap items-center gap-3 rounded-md border border-fault/40 bg-fault/10 px-4 py-3 text-sm text-text">
            <span>{errorText}</span>
            {canRetry && <Button type="button" variant="secondary" size="sm" onClick={() => void validate(textRef.current, true)}>{t('errorRetry')}</Button>}
          </div>
        )}
      </section>

      {report && (
        <section aria-labelledby="qo-report" className="rounded-lg border border-surface-border bg-bg-muted p-4">
          <h2 id="qo-report" ref={reportRef} tabIndex={-1} className="font-display text-lg font-bold text-text outline-none">{t('addedHeading')}</h2>
          <p className="mt-1 text-sm text-text-muted">{t('addedStatus', { added: fmt(added.length), failed: fmt(failed.length) })}</p>
          {added.length > 0 && (
            <ul className="mt-3 space-y-1 text-sm text-text">
              {added.map((o, i) => <li key={`a${i}`}>{t('addedLine', { part: o.item.label, qty: fmt(o.item.quantity) })}</li>)}
            </ul>
          )}
          {failed.length > 0 && (
            <>
              <h3 className="mt-4 text-sm font-semibold text-text">{t('failedHeading')}</h3>
              <ul className="mt-1 space-y-1 text-sm text-text">
                {failed.map((o, i) => <li key={`f${i}`}>{o.item.label}: {t(`fail_${['insufficientStock', 'cartFull', 'rateLimited', 'unavailable', 'notFound'].includes(o.reason) ? o.reason : 'other'}` as 'fail_other')}</li>)}
              </ul>
            </>
          )}
          {added.length > 0 && (
            <Link prefetch={false} href={`${prefix}/cart`} className="mt-4 inline-flex h-11 items-center rounded-md bg-accent px-6 text-sm font-semibold text-accent-fg hover:bg-accent-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent">
              {t('goToCart')}
            </Link>
          )}
        </section>
      )}

      {checked && groups && sum && (
        <section aria-labelledby="qo-result" aria-busy={busy} className={busy ? 'opacity-70 transition-opacity' : 'transition-opacity'}>
          <h2 id="qo-result" ref={resultRef} tabIndex={-1} className="mb-3 font-display text-xl font-bold text-text outline-none">{t('resultHeading')}</h2>
          {stale && <p className="mb-3 rounded-md border border-status-pending/50 bg-status-pending/10 px-3 py-2 text-sm text-text">{t('staleNote')}</p>}

          {groups.attention.length > 0 && (
            <div className="mb-6">
              <h3 className="mb-2 font-semibold text-text">{t('attentionHeading')} ({fmt(groups.attention.length)})</h3>
              <RowHeader t={t} />
              <ul className="rounded-lg border border-surface-border md:rounded-t-none">
                {groups.attention.map((r) =>
                  r.kind === 'line' ? (
                    <ResultRow key={`l${r.line.lineNumbers[0]}-${r.line.raw}`} line={r.line} a={actions} t={t} partLabel={t('colProduct')} />
                  ) : (
                    <ErrorRow key={`e${r.issue.lineNumber}-${lineText(r.issue.lineNumber)}`} issue={r.issue} rawText={lineText(r.issue.lineNumber)} a={actions} t={t} />
                  )
                )}
              </ul>
            </div>
          )}

          {groups.valid.length > 0 && (
            <div>
              <h3 className="mb-2 font-semibold text-text">{t('validHeading')} ({fmt(groups.valid.length)})</h3>
              <RowHeader t={t} />
              <ul className="rounded-lg border border-surface-border md:rounded-t-none">
                {groups.valid.map((l) => (
                  <ResultRow key={`v${l.lineNumbers[0]}-${l.variant ? variantPartText(l.variant) : l.raw}`} line={l} a={actions} t={t} partLabel={t('colProduct')} />
                ))}
              </ul>
            </div>
          )}

          <div className="sticky bottom-0 z-20 mt-6 rounded-t-lg border border-surface-border bg-bg p-4 shadow-lg">
            <div className="flex flex-wrap items-center gap-x-8 gap-y-3">
              <dl className="flex flex-wrap gap-x-8 gap-y-2">
                <div><dt className="text-xs text-text-muted">{t('summaryValid')}</dt><dd className="text-lg font-bold text-text">{fmt(sum.valid)}</dd></div>
                <div><dt className="text-xs text-text-muted">{t('summaryAttention')}</dt><dd className="text-lg font-bold text-text">{fmt(sum.needsAttention)}</dd></div>
                <div><dt className="text-xs text-text-muted">{t('summarySubtotal')}</dt><dd className="text-lg font-bold text-text">{formatPriceMinor(checked.result.summary.subtotalMinor, currency, locale)}</dd></div>
              </dl>
              <div className="ml-auto flex w-full flex-col gap-1 md:w-auto md:items-end">
                <Button id="qo-add" type="button" variant="accent" size="lg" disabled={addDisabled} onClick={() => void addAll(cartBatch)} aria-describedby="qo-add-note">
                  {adding ? t('adding', { done: fmt(adding.done), total: fmt(adding.total) }) : cartBatch.length === 0 ? t('addNone') : t('addButton', { count: cartBatch.length, n: fmt(cartBatch.length) })}
                </Button>
                <p id="qo-add-note" className="text-xs text-text-muted md:max-w-sm md:text-right">{t('addNote')} {t('subtotalNote')}</p>
              </div>
            </div>
          </div>
        </section>
      )}
    </div>
  );
}
