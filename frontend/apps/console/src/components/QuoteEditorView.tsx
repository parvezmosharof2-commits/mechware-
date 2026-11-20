'use client';

import * as React from 'react';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import { Textarea } from '@mechware/ui/src/components/Textarea';
import {
  QUOTE_EDITOR_MAX_LINES, QUOTE_PREVIEW_DELAY_MS, QUOTE_VALIDITY_USUAL, addLineProblem, buildDraftBody, canEditQuotes, contactLocked, discountWords, editableVersion,
  editorErrorText, editorFailureKind, editorProblems, formFromVersion, isDirty, lineNeedsReason, lineProblem, newLineFor, nextEditorKey, outcomeWords, previewFlagWords,
  previewHasCost, previewKey, previewLineNotes, previewProblemText, quoteStatusLabel, referenceSourceText, removeLine, sendButtonState, sortServerFields, takaShown,
  type EditorForm, type EditorLine, type QuotePricePreview, type QuoteSendResult, type StaffQuoteDetail, type VariantPick
} from '@mechware/api-client';
import { deskError, lookupsApi, staffQuotesApi } from '../lib/api';
import { issueError } from '../lib/quoteIssue';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { BackLink, FailureAlert, FocusAlert, Loading, NoAccess, Notice, useFocusOn } from './CreditShared';
import { SellerPicker } from './SellerPicker';

// Loaded when first needed, so the page's first load stays small (the dialog library is heavy).
const SendConfirmDialog = React.lazy(() => import('./QuoteIssueShared').then((m) => ({ default: m.SendConfirmDialog })));
const SendResultPanel = React.lazy(() => import('./QuoteIssueShared').then((m) => ({ default: m.SendResultPanel })));

const CELL = 'w-full rounded-md border border-surface-border bg-surface px-2 py-1 text-sm';
const SELECT = 'w-full rounded-md border border-surface-border bg-surface px-3 py-2 text-sm';

type PreviewState = 'invalid' | 'waiting' | 'loading' | 'ready' | 'failed';

function PartSearch({ lines, disabled, onAdd }: { lines: readonly EditorLine[]; disabled: boolean; onAdd: (v: VariantPick) => string | null }) {
  const [q, setQ] = React.useState('');
  const [busy, setBusy] = React.useState(false);
  const [found, setFound] = React.useState<VariantPick[] | null>(null);
  const [problem, setProblem] = React.useState<string | null>(null);
  const [note, setNote] = React.useState<string | null>(null);
  const find = async () => {
    if (!q.trim()) { setProblem('Type a part number, SKU or part of the title.'); return; }
    setBusy(true); setProblem(null); setNote(null);
    try { setFound((await lookupsApi.variants({ q, pageSize: 10 })).data.items.filter((v) => v.active)); } catch (err) {
      const f = deskError(err);
      setProblem(f.status === 403 ? 'Your role may not search the catalogue (it needs price-list or catalogue permission). Ask an Admin.' : f.text);
    } finally { setBusy(false); }
  };
  return (
    <section aria-labelledby="add-part-h" className="space-y-3 rounded-xl border border-surface-border bg-surface p-4" data-testid="add-part">
      <h2 id="add-part-h" className="font-display text-base font-bold">Add a part</h2>
      <div className="flex flex-wrap items-end gap-2">
        <div className="min-w-0 flex-1 basis-56">
          <Input label="Part number, SKU or title" value={q} disabled={disabled} autoComplete="off" onChange={(e) => setQ(e.target.value)} error={problem ?? undefined}
            onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); void find(); } }} />
        </div>
        <Button type="button" variant="outline" onClick={() => void find()} isLoading={busy} disabled={busy || disabled} data-testid="part-find">Find</Button>
      </div>
      {note && <p role="status" className="text-sm font-medium" data-testid="part-note">{note}</p>}
      {found && found.length === 0 && <p role="status" className="text-sm text-text-muted">Nothing matches, or the part is not for sale.</p>}
      {found && found.length > 0 && (
        <ul aria-label="Matching parts" className="max-h-56 divide-y divide-surface-border overflow-y-auto rounded-md border border-surface-border" data-testid="part-results">
          {found.map((v) => (
            <li key={v.variantId} className="flex flex-wrap items-center justify-between gap-2 p-2 text-sm">
              <span className="min-w-0 break-words"><span className="font-mono text-xs">{v.sku}</span> {v.title}{v.partNumber && <span className="block text-xs text-text-muted">Part no. {v.partNumber}</span>}</span>
              <Button type="button" size="sm" variant="outline" aria-label={`Add ${v.sku} to the quote`} disabled={disabled}
                onClick={() => { const p = addLineProblem(lines, v.variantId); if (p) { setNote(p); return; } const again = onAdd(v); setNote(again); if (!again) { setFound(null); setQ(''); } }}>Add</Button>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

function EditorBody({ id, detail, permissions, onLoadSaved }: { id: string; detail: StaffQuoteDetail; permissions: readonly string[]; onLoadSaved: () => Promise<void> }) {
  const quote = detail.quote;
  const version = quote.status === 'closed' ? null : editableVersion(detail.versions);
  const initial = React.useMemo(() => (version ? formFromVersion(version, quote.contactUserId) : null), []); // eslint-disable-line react-hooks/exhaustive-deps
  const [form, setForm] = React.useState<EditorForm | null>(initial);
  const [baseline, setBaseline] = React.useState<EditorForm | null>(initial);
  const [rowVersion, setRowVersion] = React.useState(version?.rowVersion ?? 0);
  const [attempted, setAttempted] = React.useState(false);
  const [busy, setBusy] = React.useState(false);
  const inFlight = React.useRef(false);
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const [kind, setKind] = React.useState<'stale' | 'gone' | 'other'>('other');
  const alertRef = useFocusOn(alertText);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [serverLines, setServerLines] = React.useState<Record<number, string>>({});
  const [serverHeader, setServerHeader] = React.useState<Record<string, string>>({});
  const [preview, setPreview] = React.useState<{ key: string; data: QuotePricePreview } | null>(null);
  const [previewState, setPreviewState] = React.useState<PreviewState>('invalid');
  const [previewError, setPreviewError] = React.useState<string | null>(null);
  const previewSeq = React.useRef(0);
  const [focusKey, setFocusKey] = React.useState<string | null>(null);
  const [findingSeller, setFindingSeller] = React.useState(false);
  const [loadingSaved, setLoadingSaved] = React.useState(false);
  const [sendResult, setSendResult] = React.useState<QuoteSendResult | null>(null);
  const [confirmingSend, setConfirmingSend] = React.useState(false);

  const versionNo = version?.versionNo ?? 0;
  const body = React.useMemo(() => (form ? buildDraftBody(form) : null), [form]);
  const key = body ? previewKey(body) : null;
  const problems = React.useMemo(() => (form ? editorProblems(form) : null), [form]);
  const dirty = !!form && !!baseline && isDirty(form, baseline);

  React.useEffect(() => {
    if (!body || !key || !version) { previewSeq.current++; setPreviewState('invalid'); return; }
    const seq = ++previewSeq.current;
    setPreviewState('waiting');
    const t = setTimeout(async () => {
      setPreviewState('loading');
      try {
        const r = await staffQuotesApi.previewDraft(id, versionNo, body);
        if (seq !== previewSeq.current) return;
        setPreview({ key, data: r.data });
        setPreviewError(null);
        setServerLines({});
        setPreviewState('ready');
      } catch (err) {
        if (seq !== previewSeq.current) return;
        const f = deskError(err);
        const sorted = sortServerFields(f.fields);
        setServerLines(sorted.lines);
        setPreviewError(editorErrorText(f));
        setPreviewState('failed');
      }
    }, QUOTE_PREVIEW_DELAY_MS);
    return () => clearTimeout(t);
  }, [key]); // eslint-disable-line react-hooks/exhaustive-deps

  React.useEffect(() => {
    if (!dirty) return;
    const guard = (e: BeforeUnloadEvent) => { e.preventDefault(); };
    window.addEventListener('beforeunload', guard);
    return () => window.removeEventListener('beforeunload', guard);
  }, [dirty]);

  React.useEffect(() => {
    if (!focusKey) return;
    document.getElementById(`price-${focusKey}`)?.focus();
    setFocusKey(null);
  }, [focusKey]);

  if (!version || !form || !problems) {
    return (
      <div className="space-y-4">
        <BackLink href={`/quotes/${id}`}>Back to the quote</BackLink>
        <p role="status" data-testid="not-a-draft" className="rounded-md border border-surface-border bg-surface p-4 text-sm">
          Quote {quote.number} has no draft to edit. {quote.status === 'closed' ? 'The quote is closed.' : `Its latest version is ${quoteStatusLabel(quote.status).toLowerCase()}.`} Use Revise on the quote page to start a new draft.
        </p>
      </div>
    );
  }

  const set = (patch: Partial<EditorForm>) => { setForm({ ...form, ...patch }); setNotice(null); };
  const setLine = (k: string, patch: Partial<EditorLine>) => set({ lines: form.lines.map((l) => (l.key === k ? { ...l, ...patch } : l)) });
  const current = preview && preview.key === key ? preview.data : null;
  const shown = preview?.data ?? null;
  const showCost = previewHasCost(shown);
  const notes = shown ? previewLineNotes(shown) : new Map<number, string[]>();
  const locked = contactLocked(detail.versions);
  const header = { ...serverHeader, ...problems.header };
  const longValidity = /^\d+$/.test(form.validityDays.trim()) && Number(form.validityDays.trim()) > QUOTE_VALIDITY_USUAL;

  const addLine = (v: VariantPick): string | null => {
    const p = addLineProblem(form.lines, v.variantId);
    if (p) return p;
    const line = newLineFor(v);
    set({ lines: [...form.lines, line] });
    setFocusKey(line.key);
    return null;
  };

  const save = async () => {
    if (inFlight.current) return;
    setAttempted(true);
    setAlertText(null);
    setNotice(null);
    const b = buildDraftBody(form, rowVersion);
    if (!b) { setKind('other'); setAlertText(`Fix the ${problems.count} marked field${problems.count === 1 ? '' : 's'} first. Nothing was saved.`); return; }
    inFlight.current = true;
    setBusy(true);
    try {
      const r = await staffQuotesApi.saveDraft(id, versionNo, b);
      const saved = editableVersion(r.data.versions);
      setRowVersion(saved?.rowVersion ?? rowVersion);
      setBaseline(form);
      setServerLines({});
      setServerHeader({});
      setNotice(`Saved. Version ${versionNo} has ${form.lines.length} line${form.lines.length === 1 ? '' : 's'}. It is still a draft: nothing has gone to the buyer.`);
    } catch (err) {
      const f = deskError(err);
      const sorted = sortServerFields(f.fields);
      setServerLines(sorted.lines);
      setServerHeader(sorted.header);
      setKind(editorFailureKind(f.code));
      setAlertText(editorErrorText(f));
    } finally { inFlight.current = false; setBusy(false); }
  };

  const sendNow = async () => {
    if (inFlight.current) return;
    inFlight.current = true;
    setBusy(true);
    setAlertText(null);
    try {
      const r = await staffQuotesApi.sendVersion(id, versionNo);
      setConfirmingSend(false);
      setSendResult(r.data);
    } catch (err) {
      const f = issueError(err);
      setConfirmingSend(false);
      setKind(f.reload ? 'gone' : 'other');
      setAlertText(f.text);
    } finally { inFlight.current = false; setBusy(false); }
  };

  const loadSaved = async () => {
    setLoadingSaved(true);
    try { await onLoadSaved(); } catch (err) { setKind('other'); setAlertText(editorErrorText(deskError(err))); setLoadingSaved(false); }
  };

  const lineError = (l: EditorLine, n: number): string | undefined => {
    const own = lineProblem(l);
    if (own && (attempted || l.priceText.trim() !== '' || l.quantity.trim() !== '1')) return own;
    return serverLines[n];
  };

  if (sendResult) {
    return (
      <div className="space-y-4">
        <p className="text-sm" data-testid="editor-heading">Quote <span className="font-mono">{quote.number}</span> for {quote.companyName}</p>
        <React.Suspense fallback={null}><SendResultPanel result={sendResult} quoteId={id} /></React.Suspense>
      </div>
    );
  }
  const send = sendButtonState({
    canEdit: canEditQuotes(permissions), dirty, lineCount: form.lines.length, valid: body !== null,
    checking: previewState === 'waiting' || previewState === 'loading', outcome: previewState === 'failed' ? 'refused' : current?.outcome ?? null
  });

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <BackLink href={`/quotes/${id}`}>Back to the quote</BackLink>
        <p className="text-sm" data-testid="editor-heading">Quote <span className="font-mono">{quote.number}</span> for {quote.companyName}: version {versionNo}, draft</p>
      </div>
      {notice && <Notice testId="editor-notice">{notice}</Notice>}
      {alertText && (
        <FocusAlert ref={alertRef} testId="editor-alert">
          <p>{alertText}</p>
          {kind === 'stale' && (
            <div className="mt-2 flex flex-wrap gap-2">
              <Button type="button" size="sm" variant="outline" onClick={() => void loadSaved()} isLoading={loadingSaved} disabled={loadingSaved} data-testid="load-saved">Load the saved version (replaces my edits)</Button>
              <Button type="button" size="sm" variant="ghost" onClick={() => setAlertText(null)}>Keep my edits and carry on</Button>
            </div>
          )}
          {kind === 'gone' && <p className="mt-2"><BackLink href={`/quotes/${id}`}>Go to the quote</BackLink></p>}
        </FocusAlert>
      )}

      <form noValidate onSubmit={(e) => { e.preventDefault(); void save(); }} className="space-y-6" aria-label="Quote draft">
        <section aria-labelledby="terms-h" className="space-y-4 rounded-xl border border-surface-border bg-surface p-4">
          <h2 id="terms-h" className="font-display text-base font-bold">Terms</h2>
          <div className="grid gap-4 sm:grid-cols-2">
            <div>
              <Input label="Valid for (days, 1 to 90)" inputMode="numeric" value={form.validityDays} onChange={(e) => set({ validityDays: e.target.value })} error={header.validityDays} data-testid="validity-days" />
              {longValidity && !header.validityDays && <p role="status" className="mt-1 text-xs font-medium" data-testid="validity-note">More than {QUOTE_VALIDITY_USUAL} days: a second person must approve this quote before it is sent.</p>}
            </div>
            <div className="space-y-1">
              <label htmlFor="payment-terms" className="text-sm font-medium">Payment</label>
              <select id="payment-terms" className={SELECT} value={form.paymentTerms} onChange={(e) => set({ paymentTerms: e.target.value as EditorForm['paymentTerms'] })} data-testid="payment-terms">
                <option value="as_account">As the buyer&apos;s account allows</option>
                <option value="advance">Advance payment only</option>
              </select>
            </div>
          </div>

          <fieldset className="space-y-2">
            <legend className="text-sm font-medium">Delivery charge</legend>
            <label className="flex items-center gap-2 text-sm"><input type="radio" name="shipping-mode" checked={form.shippingMode === 'calculated'} onChange={() => set({ shippingMode: 'calculated' })} data-testid="mode-calculated" /> Calculated when the buyer orders (they choose the delivery site then)</label>
            <label className="flex items-center gap-2 text-sm"><input type="radio" name="shipping-mode" checked={form.shippingMode === 'fixed'} onChange={() => set({ shippingMode: 'fixed' })} data-testid="mode-fixed" /> Fixed: the company&apos;s delivery site and one fee for each seller</label>
          </fieldset>
          {form.shippingMode === 'fixed' && (
            <div className="space-y-3 rounded-md border border-surface-border p-3" data-testid="fixed-delivery">
              <Input label="Delivery site of the company (site id)" value={form.shipToSiteId} onChange={(e) => set({ shipToSiteId: e.target.value })} error={header.shipToSiteId} autoComplete="off" spellCheck={false} data-testid="site-id"
                helperText="It must be one of this company's sites. There is no staff-side site list yet, so paste the site id (the buyer sees it in the company page)." />
              <div className="space-y-2">
                <p className="text-sm font-medium" id="fees-h">Delivery fee for each seller (BDT, VAT as for goods)</p>
                {header.shippingFees && <p role="alert" className="text-xs font-medium text-fault">{header.shippingFees}</p>}
                {form.fees.length > 0 && (
                  <table className="relative w-full text-left text-sm" data-testid="fee-table">
                    <caption className="sr-only">Delivery fees, one per seller</caption>
                    <thead className="text-xs text-text-muted"><tr><th scope="col" className="py-1 pr-2">Seller</th><th scope="col" className="py-1 pr-2">Fee (BDT)</th><th scope="col" className="py-1"><span className="sr-only">Remove</span></th></tr></thead>
                    <tbody>
                      {form.fees.map((fee, i) => (
                        <tr key={fee.key} className="align-top">
                          <td className="py-1 pr-2">
                            <input aria-label={`Seller id for fee ${i + 1}`} className={CELL + ' font-mono text-xs'} value={fee.sellerId} autoComplete="off" spellCheck={false}
                              onChange={(e) => set({ fees: form.fees.map((x) => (x.key === fee.key ? { ...x, sellerId: e.target.value } : x)) })} />
                            {fee.label && <span className="block text-xs text-text-muted">{fee.label}</span>}
                          </td>
                          <td className="py-1 pr-2">
                            <input aria-label={`Fee in taka for seller ${fee.label || i + 1}`} className={CELL + ' font-mono'} inputMode="decimal" value={fee.feeText} data-testid="fee-amount"
                              onChange={(e) => set({ fees: form.fees.map((x) => (x.key === fee.key ? { ...x, feeText: e.target.value } : x)) })} />
                          </td>
                          <td className="py-1"><Button type="button" size="sm" variant="ghost" aria-label={`Remove the fee for seller ${fee.label || i + 1}`} onClick={() => set({ fees: form.fees.filter((x) => x.key !== fee.key) })}>Remove</Button></td>
                          {problems.fees[i] && <td role="alert" className="py-1 text-xs font-medium text-fault">{problems.fees[i]}</td>}
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
                <div className="flex flex-wrap gap-2">
                  <Button type="button" size="sm" variant="outline" onClick={() => set({ fees: [...form.fees, { key: nextEditorKey('f'), sellerId: '', label: '', feeText: '' }] })} data-testid="add-fee">Add a fee</Button>
                  <Button type="button" size="sm" variant="ghost" aria-expanded={findingSeller} onClick={() => setFindingSeller(!findingSeller)}>{findingSeller ? 'Hide the seller search' : 'Find a seller by store name'}</Button>
                </div>
                {findingSeller && (
                  <SellerPicker onChange={(sellerId, name) => {
                    if (!sellerId || form.fees.some((x) => x.sellerId.toLowerCase() === sellerId.toLowerCase())) return;
                    set({ fees: [...form.fees, { key: nextEditorKey('f'), sellerId, label: name, feeText: '' }] });
                  }} />
                )}
                <p className="text-xs text-text-muted">The server needs exactly one fee for each seller whose goods are on the quote, and says so in the preview. Fees saved earlier are not shown again when the page opens: enter them again before saving.</p>
              </div>
            </div>
          )}

          <Input label="Buyer contact (user id)" value={form.contactUserId} onChange={(e) => set({ contactUserId: e.target.value })} error={header.contactUserId} disabled={locked} autoComplete="off" spellCheck={false} data-testid="contact-id"
            helperText={locked ? 'The contact is locked while a version waits for approval, is sent or is accepted.' : `Now: ${quote.contactName}. Change it only to another current Buyer or Admin member of the company; the server checks it.`} />
          <Textarea label="Terms (the buyer sees them)" value={form.termsText} onChange={(e) => set({ termsText: e.target.value })} rows={3} maxLength={4000} error={header.termsText} />
          <Textarea label="Note to the buyer" value={form.buyerNote} onChange={(e) => set({ buyerNote: e.target.value })} rows={2} maxLength={2000} error={header.buyerNote} />
          <Textarea label="Internal note (the buyer does not see it)" value={form.internalNote} onChange={(e) => set({ internalNote: e.target.value })} rows={2} maxLength={2000} error={header.internalNote} />
        </section>

        <section aria-labelledby="lines-h" className="space-y-3 rounded-xl border border-surface-border bg-surface p-4">
          <h2 id="lines-h" className="font-display text-base font-bold">Lines ({form.lines.length} of {QUOTE_EDITOR_MAX_LINES})</h2>
          {header.lines && <p role="alert" className="text-sm font-medium text-fault">{header.lines}</p>}
          {form.lines.length === 0 ? (
            <p className="text-sm text-text-muted" data-testid="no-lines">No line yet. Find a part below and add it.</p>
          ) : (
            <div className="relative overflow-x-auto rounded-md border border-surface-border">
              <table className="w-full text-left text-sm" data-testid="editor-lines">
                <caption className="sr-only">Lines of the draft. Quantity and net price are editable. The minimum order and pack size are checked by the server and any problem shows under the quantity.</caption>
                <thead className="bg-bg-subtle text-xs text-text-muted">
                  <tr>
                    <th scope="col" className="px-3 py-2">No.</th>
                    <th scope="col" className="px-3 py-2">Part</th>
                    <th scope="col" className="px-3 py-2">Quantity</th>
                    <th scope="col" className="px-3 py-2">Net price (BDT, before VAT)</th>
                    <th scope="col" className="px-3 py-2 text-right">Buyer&apos;s own price now</th>
                    {showCost && <th scope="col" className="px-3 py-2 text-right" data-testid="cost-head">Cost / floor (BDT)</th>}
                    <th scope="col" className="px-3 py-2">Reason and note</th>
                    <th scope="col" className="px-3 py-2"><span className="sr-only">Remove</span></th>
                  </tr>
                </thead>
                <tbody>
                  {form.lines.map((l, i) => {
                    const n = i + 1;
                    const pl = shown?.lines.find((x) => x.lineNo === n && x.variantId === l.variantId) ?? null;
                    const err = lineError(l, n);
                    const lineNotes = notes.get(n) ?? [];
                    const needsReason = lineNeedsReason(shown, n) || l.reason.trim().length > 0 || lineNotes.some((t) => t.startsWith('Below'));
                    const discount = pl ? discountWords(pl.unitPriceMinor, pl.referenceUnitPriceMinor) : '';
                    return (
                      <tr key={l.key} className="border-t border-surface-border align-top" data-testid="editor-line" data-line-no={n} data-sku={l.sku}>
                        <td className="px-3 py-2">{n}</td>
                        <td className="px-3 py-2"><span className="font-mono text-xs">{l.sku}</span> {l.title}{l.partNumber && <span className="block text-xs text-text-muted">Part no. {l.partNumber}</span>}</td>
                        <td className="px-3 py-2">
                          <input aria-label={`Quantity for line ${n}, ${l.sku}`} className={CELL + ' w-24 font-mono'} inputMode="numeric" value={l.quantity} aria-invalid={!!err} aria-describedby={err ? `err-${l.key}` : undefined}
                            onChange={(e) => setLine(l.key, { quantity: e.target.value })} data-testid="line-qty" />
                        </td>
                        <td className="px-3 py-2">
                          <input id={`price-${l.key}`} aria-label={`Net price in taka for line ${n}, ${l.sku}`} className={CELL + ' w-32 font-mono'} inputMode="decimal" value={l.priceText} aria-invalid={!!err} aria-describedby={err ? `err-${l.key}` : undefined}
                            placeholder="1250.50" onChange={(e) => setLine(l.key, { priceText: e.target.value })} data-testid="line-price" />
                          {pl && <span className="mt-1 block text-xs text-text-muted" data-testid="line-vat">VAT {pl.vatRate}%: total {takaShown(pl.lineTotalMinor)}</span>}
                          {discount && <span className="mt-1 block text-xs font-medium" data-testid="line-discount">{discount}</span>}
                          {lineNotes.length > 0 && <span className="mt-1 block text-xs font-bold text-fault" data-testid="line-flags">{lineNotes.join('. ')}</span>}
                        </td>
                        <td className="px-3 py-2 text-right font-mono text-xs" data-testid="line-reference">
                          {pl && pl.referenceUnitPriceMinor !== null ? <>{takaShown(pl.referenceUnitPriceMinor)}<span className="block font-sans text-text-muted">{referenceSourceText(pl.referenceSource)}</span></> : ''}
                        </td>
                        {showCost && <td className="px-3 py-2 text-right font-mono text-xs" data-testid="cost-cell">{pl?.cost ? `${takaShown(pl.cost.costMinorAtCheck) || 'no cost'} / ${takaShown(pl.cost.floorMinorAtCheck) || 'no floor'}` : ''}</td>}
                        <td className="px-3 py-2">
                          {needsReason && (
                            <div>
                              <input aria-label={`Reason for the price on line ${n}, ${l.sku}`} className={CELL} value={l.reason} onChange={(e) => setLine(l.key, { reason: e.target.value })} data-testid="line-reason"
                                placeholder={lineNeedsReason(shown, n) ? 'Required: why below the floor' : 'Why below the floor'} aria-invalid={lineNeedsReason(shown, n) && l.reason.trim().length < 5} />
                              {lineNeedsReason(shown, n) && l.reason.trim().length < 5 && <p className="mt-1 text-xs font-medium text-fault" data-testid="reason-needed">This price is below the margin floor: a reason of at least 5 characters is needed before it can be sent.</p>}
                            </div>
                          )}
                          <input aria-label={`Note on line ${n}, ${l.sku} (the buyer sees it)`} className={CELL + (needsReason ? ' mt-1' : '')} value={l.lineNote} placeholder="Note (optional)" onChange={(e) => setLine(l.key, { lineNote: e.target.value })} />
                          {err && <p id={`err-${l.key}`} role="alert" className="mt-1 text-xs font-medium text-fault" data-testid="line-error">{err}</p>}
                        </td>
                        <td className="whitespace-nowrap px-3 py-2">
                          <Button type="button" size="sm" variant="ghost" aria-label={`Remove line ${n}, ${l.sku}`} onClick={() => set({ lines: removeLine(form.lines, l.key) })} data-testid="remove-line">Remove</Button>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
        </section>

        <PartSearch lines={form.lines} disabled={busy || form.lines.length >= QUOTE_EDITOR_MAX_LINES} onAdd={addLine} />

        <section aria-labelledby="preview-h" className="space-y-3 rounded-xl border border-surface-border bg-surface p-4" aria-busy={previewState === 'waiting' || previewState === 'loading'} data-testid="preview">
          <h2 id="preview-h" className="font-display text-base font-bold">Price check (nothing is saved by it)</h2>
          <p role="status" className="text-sm text-text-muted" data-testid="preview-state">
            {previewState === 'invalid' && (problems.count > 0 ? 'Fix the marked fields to see the check.' : 'Enter a quantity and a net price on every line to see the check.')}
            {(previewState === 'waiting' || previewState === 'loading') && 'Checking the prices…'}
            {previewState === 'ready' && current && 'Up to date with what you typed.'}
            {previewState === 'failed' && 'The check could not be completed.'}
          </p>
          {previewError && previewState === 'failed' && <p role="alert" className="text-sm font-medium text-fault" data-testid="preview-error">{previewError}</p>}
          {shown && (
            <div className={previewState === 'waiting' || previewState === 'loading' || !current ? 'opacity-60' : ''}>
              {(() => {
                const o = outcomeWords(shown.outcome);
                return <p className={'text-sm font-semibold' + (o.tone === 'refused' ? ' text-fault' : '')} data-testid="preview-outcome" data-outcome={shown.outcome}>{o.text}</p>;
              })()}
              {shown.problems.length > 0 && (
                <ul aria-label="Problems that stop a send" className="mt-2 space-y-1 rounded-md border border-fault p-3 text-sm font-medium text-fault" data-testid="preview-problems">
                  {shown.problems.map((p) => <li key={p} data-code={p}>{previewProblemText(p)}</li>)}
                </ul>
              )}
              {previewFlagWords(shown).length > 0 && (
                <ul aria-label="Checks that need a decision" className="mt-2 space-y-1 rounded-md border border-surface-border p-3 text-sm font-medium" data-testid="preview-flags">
                  {previewFlagWords(shown).map((f) => <li key={f}>{f}</li>)}
                </ul>
              )}
              <dl className="ml-auto mt-3 grid max-w-sm grid-cols-2 gap-x-4 gap-y-1 text-sm" data-testid="preview-totals">
                <dt>Goods, net</dt><dd className="text-right font-mono">BDT {takaShown(shown.goodsNetMinor)}</dd>
                <dt>VAT</dt><dd className="text-right font-mono">BDT {takaShown(shown.taxMinor)}</dd>
                <dt>Goods, total</dt><dd className="text-right font-mono">BDT {takaShown(shown.goodsTotalMinor)}</dd>
                <dt>Delivery</dt><dd className="text-right font-mono">{shown.shippingTotalMinor === null ? 'Worked out when the buyer orders' : `BDT ${takaShown(shown.shippingTotalMinor)}`}</dd>
                {shown.grandTotalMinor !== null && <><dt className="font-semibold">Total</dt><dd className="text-right font-mono font-semibold">BDT {takaShown(shown.grandTotalMinor)}</dd></>}
              </dl>
            </div>
          )}
        </section>

        <div className="flex flex-wrap items-center gap-3">
          <Button type="submit" isLoading={busy} disabled={busy} data-testid="save-draft">Save the draft</Button>
          <Button type="button" variant="outline" disabled={busy || !send.allowed} aria-describedby="send-note" data-testid="send-quote" onClick={() => { setAlertText(null); setConfirmingSend(true); }}>{send.label}</Button>
          <span className="text-xs text-text-muted" id="send-note" data-testid="send-note">{send.reason}</span>
          {dirty && <span role="status" className="text-xs font-medium" data-testid="dirty-note">You have unsaved changes.</span>}
        </div>
      </form>

      {confirmingSend && <React.Suspense fallback={null}><SendConfirmDialog open busy={busy} onCancel={() => setConfirmingSend(false)} onConfirm={() => void sendNow()}
        title={outcomeIsApproval(current?.outcome) ? `Send version ${versionNo} for approval?` : `Send version ${versionNo} to the buyer?`}
        confirmLabel={outcomeIsApproval(current?.outcome) ? 'Send for approval' : 'Send to the buyer'}
        description={outcomeIsApproval(current?.outcome)
          ? 'A second person must approve it before the buyer sees anything. Until then you can recall it. The prices are checked again by the server.'
          : 'The buyer can see and accept this version as soon as it is sent. It is valid from now for the days you set, and a version still out with the buyer is replaced. The prices are checked again by the server.'} /></React.Suspense>}
    </div>
  );
}

const outcomeIsApproval = (o: string | undefined): boolean => o === 'approval';

function Editor({ id, permissions }: { id: string; permissions: readonly string[] }) {
  const res = useResource(() => staffQuotesApi.quote(id), id);
  const [fresh, setFresh] = React.useState<{ detail: StaffQuoteDetail; epoch: number } | null>(null);
  const detail = fresh?.detail ?? res.data?.data ?? null;
  const failure = res.error ? deskError(res.error) : null;
  const loadSaved = React.useCallback(async () => {
    const r = await staffQuotesApi.quote(id);
    setFresh((p) => ({ detail: r.data, epoch: (p?.epoch ?? 0) + 1 }));
  }, [id]);
  if (failure) return <FailureAlert failure={failure} onRetry={res.reload} />;
  if (!detail) return <Loading />;
  return <EditorBody key={fresh?.epoch ?? 0} id={id} detail={detail} permissions={permissions} onLoadSaved={loadSaved} />;
}

export function QuoteEditorView({ id }: { id: string }) {
  const { user } = useAuth();
  const permissions = user?.permissions ?? [];
  return (
    <ConsoleShell activeItemId="quotes-list" title="Edit quote draft">
      {canEditQuotes(permissions) ? <Editor id={id} permissions={permissions} /> : (
        <div className="space-y-3">
          <NoAccess permission="quotes.edit" />
          <BackLink href={`/quotes/${id}`}>Back to the quote</BackLink>
        </div>
      )}
    </ConsoleShell>
  );
}
