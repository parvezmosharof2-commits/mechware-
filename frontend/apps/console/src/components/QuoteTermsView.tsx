'use client';

import * as React from 'react';
import { Button } from '@mechware/ui/src/components/Button';
import {
  TERMS_MAX_CHARS, TERMS_MAX_LINES, canAddTerm, canChangeTerms, dhakaWhen, formFromTerms, moveTerm, newTermLine, removeTerm, setTermText,
  termCounter, termLineProblem, termsBody, termsDirty, termsFailureKind, termsProblems,
  type QuoteTerms, type TermsForm, type TermsLang
} from '@mechware/api-client';
import { quoteTermsApi, termsError } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { FailureAlert, FocusAlert, Loading, NoAccess, Notice, useFocusOn } from './CreditShared';

const AREA = 'w-full rounded-md border border-surface-border bg-surface px-2 py-1 text-sm';
const LISTS: { lang: TermsLang; name: string; hint: string }[] = [
  { lang: 'en', name: 'English', hint: 'Printed on the English quote PDF.' },
  { lang: 'bn', name: 'Bangla', hint: 'Printed on the Bangla quote PDF.' }
];

function TermList({ lang, name, hint, form, editable, shown, onChange }: {
  lang: TermsLang; name: string; hint: string; form: TermsForm; editable: boolean; shown: boolean; onChange: (lines: TermsForm[TermsLang]) => void;
}) {
  const lines = form[lang];
  const [focusKey, setFocusKey] = React.useState<number | null>(null);
  React.useEffect(() => {
    if (focusKey !== null) { document.getElementById(`terms-${lang}-${focusKey}`)?.focus(); setFocusKey(null); }
  }, [focusKey, lang]);
  return (
    <section aria-labelledby={`terms-${lang}-h`} className="space-y-3 rounded-xl border border-surface-border bg-surface p-4" data-testid={`terms-${lang}`}>
      <div>
        <h2 id={`terms-${lang}-h`} className="font-display text-base font-bold">{name} terms</h2>
        <p className="text-xs text-text-muted">{hint} Up to {TERMS_MAX_LINES} lines of {TERMS_MAX_CHARS} characters. <code>{'{validUntil}'}</code> becomes the quote&apos;s last valid date.</p>
      </div>
      {lines.length === 0 && <p role="status" className="text-sm text-text-muted" data-testid={`terms-${lang}-empty`}>No {name} lines. Quotes would print no {name} terms.</p>}
      <ol className="space-y-3">
        {lines.map((l, i) => {
          const problem = shown ? termLineProblem(l.text) : null;
          const id = `terms-${lang}-${l.key}`;
          const label = `${name} line ${i + 1}`;
          return (
            <li key={l.key} className="space-y-1" data-testid={`terms-${lang}-line`}>
              <label htmlFor={id} className="text-xs font-semibold text-text-muted">{label}</label>
              <textarea
                id={id} rows={2} className={AREA} value={l.text} readOnly={!editable} aria-invalid={problem ? true : undefined}
                aria-describedby={`${id}-count${problem ? ` ${id}-err` : ''}`}
                lang={lang} onChange={(e) => onChange(setTermText(lines, i, e.target.value))}
              />
              <div className="flex flex-wrap items-center justify-between gap-2">
                <span id={`${id}-count`} className={`text-xs ${l.text.trim().length > TERMS_MAX_CHARS ? 'font-semibold text-fault' : 'text-text-muted'}`} data-testid={`${id}-count`}>{termCounter(l.text)}</span>
                {editable && (
                  <span className="flex flex-wrap gap-2">
                    <Button type="button" size="sm" variant="outline" disabled={i === 0} aria-label={`Move ${label} up`} onClick={() => onChange(moveTerm(lines, i, -1))}>Up</Button>
                    <Button type="button" size="sm" variant="outline" disabled={i === lines.length - 1} aria-label={`Move ${label} down`} onClick={() => onChange(moveTerm(lines, i, 1))}>Down</Button>
                    <Button type="button" size="sm" variant="ghost" aria-label={`Remove ${label}`} onClick={() => onChange(removeTerm(lines, i))}>Remove</Button>
                  </span>
                )}
              </div>
              {problem && <p id={`${id}-err`} className="text-xs font-medium text-fault">{problem}</p>}
            </li>
          );
        })}
      </ol>
      {editable && (
        <div className="flex flex-wrap items-center gap-3">
          <Button type="button" size="sm" variant="outline" disabled={!canAddTerm(lines)} data-testid={`terms-${lang}-add`}
            onClick={() => { const n = newTermLine(); onChange([...lines, n]); setFocusKey(n.key); }}>Add a {name} line</Button>
          {!canAddTerm(lines) && <span className="text-xs text-text-muted">The most is {TERMS_MAX_LINES} lines.</span>}
        </div>
      )}
    </section>
  );
}

function Editor({ saved, editable, reload }: { saved: QuoteTerms; editable: boolean; reload: () => void }) {
  const [base, setBase] = React.useState(saved);
  const [baseForm, setBaseForm] = React.useState(() => formFromTerms(saved));
  const [form, setForm] = React.useState(baseForm);
  const [attempted, setAttempted] = React.useState(false);
  const [busy, setBusy] = React.useState(false);
  const inFlight = React.useRef(false);
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const [theirs, setTheirs] = React.useState<QuoteTerms | null>(null);
  const [lostTheirs, setLostTheirs] = React.useState(false);
  const [notice, setNotice] = React.useState<string | null>(null);
  const alertRef = useFocusOn(alertText);
  const dirty = termsDirty(form, baseForm);
  const problems = termsProblems(form);

  const adopt = (t: QuoteTerms) => {
    const f = formFromTerms(t);
    setBase(t); setBaseForm(f); setForm(f); setTheirs(null); setAlertText(null); setAttempted(false);
  };
  const save = async () => {
    if (inFlight.current) return;
    setAttempted(true); setAlertText(null); setNotice(null); setTheirs(null); setLostTheirs(false);
    const body = termsBody(form, base.rowVersion);
    if (!body) { setAlertText(`Fix the ${problems.count} marked line${problems.count === 1 ? '' : 's'} first. Nothing was saved. ${problems.first ?? ''}`); return; }
    inFlight.current = true; setBusy(true);
    try {
      const r = await quoteTermsApi.save(body);
      const f = formFromTerms(r.data);
      setBase(r.data); setBaseForm(f); setForm(f); setAttempted(false);
      setNotice('Saved. Quotes sent from now on print these lines. Quotes already sent keep the lines they were sent with.');
    } catch (err) {
      const e = termsError(err);
      setAlertText(e.text);
      if (termsFailureKind(e.code) === 'stale') {
        try { setTheirs((await quoteTermsApi.get()).data); } catch { setLostTheirs(true); }
      }
    } finally { inFlight.current = false; setBusy(false); }
  };
  const showTheirs = (t: QuoteTerms) => (
    <details className="mt-2 text-xs font-normal">
      <summary className="cursor-pointer font-semibold">What they saved ({t.termsEn.length} English, {t.termsBn.length} Bangla lines)</summary>
      <div className="mt-1 grid gap-2 sm:grid-cols-2">
        {([['English', t.termsEn], ['Bangla', t.termsBn]] as const).map(([n, ls]) => (
          <div key={n}><p className="font-semibold">{n}</p><ol className="list-decimal pl-5">{ls.map((x, i) => <li key={i} lang={n === 'Bangla' ? 'bn' : 'en'}>{x}</li>)}</ol></div>
        ))}
      </div>
    </details>
  );

  return (
    <form noValidate aria-label="Default quote terms" onSubmit={(e) => { e.preventDefault(); if (editable) void save(); }} className="space-y-5">
      {notice && <Notice testId="terms-notice">{notice}</Notice>}
      {alertText && (
        <FocusAlert ref={alertRef} testId="terms-alert">
          <p>{alertText}</p>
          {theirs && (
            <>
              {showTheirs(theirs)}
              <div className="mt-2 flex flex-wrap gap-2">
                <Button type="button" size="sm" variant="outline" data-testid="terms-load-theirs" onClick={() => adopt(theirs)}>Load their version (replaces my edits)</Button>
                <Button type="button" size="sm" variant="outline" data-testid="terms-overwrite"
                  onClick={() => { setBase(theirs); setTheirs(null); setAlertText(null); setNotice('Your lines are kept and will replace theirs. Press Save to confirm.'); }}>Keep my edits and save over theirs</Button>
              </div>
            </>
          )}
          {lostTheirs && <Button type="button" size="sm" variant="outline" className="mt-2" onClick={reload}>Reload the saved terms</Button>}
        </FocusAlert>
      )}
      <p className="rounded-md border border-surface-border bg-bg-subtle p-3 text-sm" data-testid="terms-explain">
        These lines are printed on every quote PDF. A quote keeps the terms it was sent with: changing them here does not change a quote that has already been sent, only the quotes sent after you save.
      </p>
      {!editable && <p role="status" className="rounded-md border border-surface-border bg-bg-subtle p-3 text-sm font-medium" data-testid="terms-readonly">You can read these terms but not change them. Only roles that approve quotes (permission quotes.approve) can edit them.</p>}
      <p className="text-xs text-text-muted" data-testid="terms-meta">
        {base.updatedAtUtc ? `Last changed ${dhakaWhen(base.updatedAtUtc)}${base.updatedByName ? ` by ${base.updatedByName}` : ''}.` : 'These are the starting lines; nobody has changed them yet.'}
      </p>
      <div className="grid gap-5 lg:grid-cols-2">
        {LISTS.map((l) => (
          <TermList key={l.lang} {...l} form={form} editable={editable} shown={attempted || form[l.lang].some((x) => x.text.trim().length > TERMS_MAX_CHARS)}
            onChange={(lines) => setForm((f) => ({ ...f, [l.lang]: lines }))} />
        ))}
      </div>
      {editable && (
        <div className="flex flex-wrap items-center gap-3">
          <Button type="submit" isLoading={busy} disabled={busy || !dirty} data-testid="terms-save">Save terms</Button>
          <span className="text-xs text-text-muted" role="status">{dirty ? 'You have changes that are not saved.' : 'No changes to save.'}</span>
        </div>
      )}
    </form>
  );
}

function Body() {
  const { user } = useAuth();
  const res = useResource(() => quoteTermsApi.get(), 'quote-terms');
  const failure = res.error ? termsError(res.error) : null;
  return (
    <div className="space-y-5">
      <FailureAlert failure={failure} onRetry={res.reload} />
      {res.loading && !res.data && <Loading />}
      {res.data && <Editor saved={res.data.data} editable={canChangeTerms(user?.permissions ?? [])} reload={res.reload} />}
    </div>
  );
}

export function QuoteTermsView() {
  const { user } = useAuth();
  const canView = (user?.permissions ?? []).some((p) => p.toLowerCase() === 'quotes.view');
  return (
    <ConsoleShell activeItemId="quotes-terms" title="Quote terms">
      {canView ? <Body /> : <NoAccess permission="quotes.view" />}
    </ConsoleShell>
  );
}
