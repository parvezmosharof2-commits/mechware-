'use client';

import * as React from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { Badge, Button, Input, Skeleton } from '@mechware/ui';
import {
  PRICE_TEXT, actorName, breaksOutOfOrder, canRevise, chunkEntries, dateToUtcIso, entriesNeedingReason, entryFieldErrors, formatTaka, isEditable,
  isoToDateInput, minQtyProblem, parsePaste, priceProblem, reasonProblem, takaToMinor, validateHeader,
  type PriceListDetail, type PriceListEntry, type UpsertEntryItem
} from '@mechware/api-client';
import { priceListError, priceListsApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { resolveSkus } from '../lib/skuLookup';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { PriceListDecisionPanel } from './PriceListDecisionPanel';
import { StateBadges, fmtDate } from './PriceListsView';
import { Modal, ModalFooter } from './Modal';

interface Notice { text: string; retry: boolean }

const CELL_INPUT = 'w-full min-w-[5rem] rounded-md border border-surface-border bg-surface px-2 py-1 text-sm font-mono';

function EntryRow(props: {
  entry: PriceListEntry; scope: string; editable: boolean; showCost: boolean; flagged?: boolean; busy: boolean; serverError?: string;
  others: PriceListEntry[]; onSave: (next: UpsertEntryItem, replaceId: string | null) => Promise<boolean>; onRemove: (id: string) => void;
}) {
  const { entry, editable, showCost, busy, serverError, scope } = props;
  const [price, setPrice] = React.useState(formatTaka(entry.unitPriceMinor).replace(/,/g, ''));
  const [qty, setQty] = React.useState(String(entry.minQty));
  const [reason, setReason] = React.useState(entry.belowFloorReason ?? '');
  const [error, setError] = React.useState<string | null>(null);

  const original = formatTaka(entry.unitPriceMinor).replace(/,/g, '');
  const dirty = price !== original || qty !== String(entry.minQty) || reason.trim() !== (entry.belowFloorReason ?? '');
  const needsReason = entry.belowFloor === true && reason.trim().length < 5;

  const save = async () => {
    const minor = takaToMinor(price);
    const p = priceProblem(minor);
    if (p || minor === null) return setError(PRICE_TEXT[p ?? 'price.format']);
    const minQty = /^\d+$/.test(qty) ? Number(qty) : NaN;
    const q = minQtyProblem(minQty, scope);
    if (q) return setError(q);
    const r = reasonProblem(reason);
    if (r) return setError(r);
    if (minQty !== entry.minQty && props.others.some((o) => o.id !== entry.id && o.variantId === entry.variantId && o.minQty === minQty)) {
      return setError(`There is already a line for ${entry.sku} with minimum quantity ${minQty}. Edit that one instead.`);
    }
    setError(null);
    await props.onSave({ variantId: entry.variantId, minQty, unitPriceMinor: minor, belowFloorReason: reason.trim() || null }, minQty !== entry.minQty ? entry.id : null);
  };

  const flags = (
    <span className="flex flex-wrap gap-1">
      {entry.belowCost === true && <Badge variant="deal">Below cost</Badge>}
      {entry.belowFloor === true && entry.belowCost !== true && <Badge variant="low-stock">Below floor</Badge>}
      {entry.costUnknown === true && <Badge variant="outline">Cost unknown</Badge>}
    </span>
  );

  return (
    <tr data-testid="entry-row" data-sku={entry.sku} data-belowfloor={entry.belowFloor === true || props.flagged ? 'true' : undefined} className={'border-t border-surface-border align-top' + (props.flagged ? ' bg-bg-subtle' : '')}>
      <td className="px-3 py-2"><div className="font-mono text-xs font-semibold">{entry.sku}</div><div className="max-w-[16rem] text-xs text-text-muted">{entry.title}</div></td>
      <td className="px-3 py-2">
        {editable ? <input aria-label={`Minimum quantity for ${entry.sku}`} className={CELL_INPUT} inputMode="numeric" value={qty} onChange={(e) => setQty(e.target.value)} disabled={busy} /> : <span className="font-mono">{entry.minQty}</span>}
      </td>
      <td className="px-3 py-2">
        {editable ? <input aria-label={`Price in taka for ${entry.sku}, minimum quantity ${entry.minQty}`} className={CELL_INPUT} inputMode="decimal" value={price} onChange={(e) => setPrice(e.target.value)} disabled={busy} /> : <span className="font-mono">{formatTaka(entry.unitPriceMinor)}</span>}
      </td>
      {showCost && (
        <>
          <td className="px-3 py-2 font-mono text-xs">{entry.costMinor !== undefined ? formatTaka(entry.costMinor) : ''}</td>
          <td className="px-3 py-2 font-mono text-xs">{entry.floorMinor !== undefined ? formatTaka(entry.floorMinor) : ''}</td>
          <td className="px-3 py-2">{flags}</td>
        </>
      )}
      <td className="px-3 py-2">
        {editable ? (
          <input
            aria-label={`Below-floor reason for ${entry.sku}, minimum quantity ${entry.minQty}`}
            aria-invalid={needsReason || !!serverError}
            className={CELL_INPUT + (needsReason || serverError ? ' border-fault' : '')}
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            disabled={busy}
            placeholder={entry.belowFloor === true ? 'Required' : 'Optional'}
          />
        ) : <span className="text-xs">{entry.belowFloorReason}</span>}
        {!editable && props.flagged && !showCost && <div className="mt-1"><Badge variant="low-stock">Below floor</Badge></div>}
        {editable && needsReason && <p className="mt-1 text-xs font-medium text-fault">This entry is below the margin floor: a reason is required before submitting.</p>}
        {(error || serverError) && <p role="alert" className="mt-1 text-xs font-medium text-fault">{error ?? serverError}</p>}
      </td>
      {editable && (
        <td className="whitespace-nowrap px-3 py-2">
          <div className="flex gap-2">
            <Button type="button" size="sm" variant="outline" disabled={busy || !dirty} onClick={save}>Save</Button>
            <Button type="button" size="sm" variant="ghost" disabled={busy} onClick={() => props.onRemove(entry.id)} aria-label={`Remove ${entry.sku} minimum quantity ${entry.minQty}`}>Remove</Button>
          </div>
        </td>
      )}
    </tr>
  );
}

function AddEntry({ scope, busy, onAdd }: { scope: string; busy: boolean; onAdd: (sku: string, item: Omit<UpsertEntryItem, 'variantId'>) => Promise<string | null> }) {
  const [sku, setSku] = React.useState('');
  const [qty, setQty] = React.useState(scope === 'General' ? '2' : '1');
  const [price, setPrice] = React.useState('');
  const [reason, setReason] = React.useState('');
  const [error, setError] = React.useState<string | null>(null);
  const [working, setWorking] = React.useState(false);

  const add = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!sku.trim()) return setError('Enter the SKU.');
    const minQty = /^\d+$/.test(qty) ? Number(qty) : NaN;
    const q = minQtyProblem(minQty, scope);
    if (q) return setError(q);
    const minor = takaToMinor(price);
    const p = priceProblem(minor);
    if (p || minor === null) return setError(PRICE_TEXT[p ?? 'price.format']);
    const r = reasonProblem(reason);
    if (r) return setError(r);
    setError(null);
    setWorking(true);
    const failure = await onAdd(sku.trim(), { minQty, unitPriceMinor: minor, belowFloorReason: reason.trim() || null });
    setWorking(false);
    if (failure) setError(failure);
    else { setSku(''); setPrice(''); setReason(''); }
  };

  return (
    <form onSubmit={add} noValidate aria-label="Add one entry" className="grid grid-cols-1 items-start gap-3 sm:grid-cols-2 lg:grid-cols-5">
      <Input label="SKU" value={sku} onChange={(e) => setSku(e.target.value)} autoComplete="off" />
      <Input label="Min quantity" inputMode="numeric" value={qty} onChange={(e) => setQty(e.target.value)} />
      <Input label="Price (taka)" inputMode="decimal" value={price} onChange={(e) => setPrice(e.target.value)} placeholder="1250.50" />
      <Input label="Below-floor reason" value={reason} onChange={(e) => setReason(e.target.value)} placeholder="Optional" />
      <div className="flex items-end lg:pt-6"><Button type="submit" isLoading={working} disabled={busy || working}>Add entry</Button></div>
      {error && <p role="alert" className="text-sm font-medium text-fault sm:col-span-2 lg:col-span-5">{error}</p>}
    </form>
  );
}

function BulkPaste({ scope, busy, onAdd }: { scope: string; busy: boolean; onAdd: (items: Array<{ sku: string } & Omit<UpsertEntryItem, 'variantId'>>) => Promise<string[]> }) {
  const [text, setText] = React.useState('');
  const [messages, setMessages] = React.useState<string[]>([]);
  const [ok, setOk] = React.useState<string | null>(null);
  const [working, setWorking] = React.useState(false);

  const run = async () => {
    setOk(null);
    const parsed = parsePaste(text, scope);
    if (parsed.problems.length > 0) return setMessages(parsed.problems.slice(0, 20).map((p) => `Line ${p.line}: ${p.message}`));
    if (parsed.lines.length === 0) return setMessages(['Paste at least one line: SKU, minimum quantity, price.']);
    setMessages([]);
    setWorking(true);
    const failures = await onAdd(parsed.lines.map((l) => ({ sku: l.sku, minQty: l.minQty, unitPriceMinor: l.unitPriceMinor, belowFloorReason: l.reason })));
    setWorking(false);
    if (failures.length > 0) setMessages(failures);
    else { setOk(`${parsed.lines.length} line${parsed.lines.length === 1 ? '' : 's'} added.`); setText(''); }
  };

  return (
    <div className="space-y-2">
      <label htmlFor="paste" className="block text-sm font-semibold">Bulk entry</label>
      <p className="text-xs text-text-muted">One line per entry: <span className="font-mono">SKU, minimum quantity, price in taka</span> and an optional reason. Use tabs or semicolons if a price has a thousands comma. Nothing is saved if any line is wrong.</p>
      <textarea id="paste" rows={5} value={text} onChange={(e) => setText(e.target.value)} disabled={busy || working} className="block w-full rounded-md border border-surface-border bg-surface p-2 font-mono text-sm" placeholder={'PLC-1214C, 1, 18500.00\nPLC-1214C, 10, 17900.50'} spellCheck={false} />
      <div className="flex items-center gap-3">
        <Button type="button" variant="outline" onClick={run} isLoading={working} disabled={busy || working || !text.trim()}>Add lines</Button>
        {ok && <p role="status" className="text-sm font-medium text-success">{ok}</p>}
      </div>
      {messages.length > 0 && (
        <ul role="alert" className="list-disc space-y-1 pl-5 text-sm font-medium text-fault" data-testid="paste-problems">
          {messages.map((m) => <li key={m}>{m}</li>)}
        </ul>
      )}
    </div>
  );
}

function HeaderForm({ detail, busy, onSave }: { detail: PriceListDetail; busy: boolean; onSave: (body: { name: string; contractRef: string | null; validFromUtc: string; validToUtc: string }) => Promise<boolean> }) {
  const [name, setName] = React.useState(detail.name);
  const [ref, setRef] = React.useState(detail.contractRef ?? '');
  const [from, setFrom] = React.useState(isoToDateInput(detail.validFromUtc));
  const [to, setTo] = React.useState(isoToDateInput(detail.validToUtc));
  const [errors, setErrors] = React.useState<Record<string, string | undefined>>({});
  const company = detail.scope === 'Company';
  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    // An unchanged date keeps the stored instant; a changed one means 00:00 UTC of that date.
    const validFromUtc = from === isoToDateInput(detail.validFromUtc) ? detail.validFromUtc : dateToUtcIso(from);
    const validToUtc = to === isoToDateInput(detail.validToUtc) ? detail.validToUtc : dateToUtcIso(to);
    const found = validateHeader({ scope: detail.scope, companyId: detail.companyId, contractRef: ref, name, validFromUtc, validToUtc });
    setErrors(found);
    if (Object.keys(found).length === 0) await onSave({ name: name.trim(), contractRef: company ? ref.trim() : null, validFromUtc, validToUtc });
  };
  return (
    <form onSubmit={save} noValidate aria-label="Price list header" className="grid grid-cols-1 gap-4 md:grid-cols-2">
      <Input label="Name" required value={name} onChange={(e) => setName(e.target.value)} error={errors.name} maxLength={200} />
      {company && <Input label="Contract reference" required value={ref} onChange={(e) => setRef(e.target.value)} error={errors.contractRef} maxLength={64} />}
      <Input label="Valid from (UTC date)" type="date" required value={from} onChange={(e) => setFrom(e.target.value)} error={errors.validFromUtc} />
      <Input label="Valid to (UTC date, exclusive)" type="date" required value={to} onChange={(e) => setTo(e.target.value)} error={errors.validToUtc} />
      <div><Button type="submit" variant="outline" disabled={busy}>Save details</Button></div>
    </form>
  );
}

function Editor({ id, canEdit }: { id: string; canEdit: boolean }) {
  const router = useRouter();
  const { user } = useAuth();
  const res = useResource(() => priceListsApi.get(id), id);
  const [detail, setDetail] = React.useState<PriceListDetail | null>(null);
  const [notice, setNotice] = React.useState<Notice | null>(null);
  const [okText, setOkText] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [rowErrors, setRowErrors] = React.useState<Record<string, string>>({});
  const [dialog, setDialog] = React.useState<'submit' | 'delete' | null>(null);

  React.useEffect(() => { setDetail(res.data?.data ?? null); setNotice(null); setOkText(null); setRowErrors({}); }, [res.data]);

  /** Runs a call; on failure the notice is set and the caller's inputs stay as they are. */
  const run = async <T,>(call: () => Promise<T>, success?: string): Promise<{ ok: true; value: T } | { ok: false; fields: Record<string, string> }> => {
    setBusy(true);
    setNotice(null);
    setOkText(null);
    try {
      const value = await call();
      if (success) setOkText(success);
      return { ok: true, value };
    } catch (err) {
      const f = priceListError(err);
      setNotice({ text: f.text, retry: f.retry });
      return { ok: false, fields: f.fields };
    } finally {
      setBusy(false);
    }
  };

  if (res.loading && !detail) return <div role="status"><span className="sr-only">Loading</span><Skeleton className="h-64 w-full" /></div>;
  if (res.error && !detail) {
    const f = priceListError(res.error);
    return (
      <div role="alert" className="flex flex-wrap items-center gap-3 rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">
        <span>{f.text}</span>
        {f.retry && <Button type="button" variant="outline" size="sm" onClick={res.reload}>Try again</Button>}
        <Link href="/pricing/price-lists" className="underline">Back to the list</Link>
      </div>
    );
  }
  if (!detail) return null;

  const editable = canEdit && isEditable(detail.status);
  const entries = detail.entries;
  const showCost = entries.some((e) => e.costMinor !== undefined || e.floorMinor !== undefined || e.belowFloor !== undefined);
  const companyName = detail.companyId ? detail.companyName ?? detail.companyId : null;

  const flaggedIds = new Set(detail.belowFloorEntryIds ?? []);
  const apply = (d: PriceListDetail) => { setDetail(d); setRowErrors({}); };

  const saveRow = async (item: UpsertEntryItem, replaceId: string | null) => {
    const r = await run(async () => {
      let d = (await priceListsApi.upsertEntries(detail.id, [item])).data;
      if (replaceId) d = (await priceListsApi.deleteEntry(detail.id, replaceId)).data;
      return d;
    }, 'Entry saved.');
    if (r.ok) apply(r.value);
    return r.ok;
  };

  const removeRow = async (entryId: string) => {
    const r = await run(() => priceListsApi.deleteEntry(detail.id, entryId), 'Entry removed.');
    if (r.ok) apply(r.value.data);
  };

  const addOne = async (sku: string, item: Omit<UpsertEntryItem, 'variantId'>): Promise<string | null> => {
    setBusy(true);
    try {
      const found = (await resolveSkus([sku])).get(sku.toLowerCase());
      if (!found) return `No variant has the SKU ${sku}.`;
      const d = (await priceListsApi.upsertEntries(detail.id, [{ ...item, variantId: found.variantId }])).data;
      apply(d);
      setOkText(`${found.sku} added.`);
      return null;
    } catch (err) {
      return priceListError(err).text;
    } finally {
      setBusy(false);
    }
  };

  const addMany = async (lines: Array<{ sku: string } & Omit<UpsertEntryItem, 'variantId'>>): Promise<string[]> => {
    setBusy(true);
    try {
      const resolved = await resolveSkus(lines.map((l) => l.sku));
      const unknown = [...new Set(lines.filter((l) => !resolved.get(l.sku.toLowerCase())).map((l) => l.sku))];
      if (unknown.length > 0) return [`No variant has the SKU: ${unknown.slice(0, 20).join(', ')}${unknown.length > 20 ? ', ...' : ''}. Nothing was added.`];
      const items: UpsertEntryItem[] = lines.map((l) => ({ variantId: resolved.get(l.sku.toLowerCase())!.variantId, minQty: l.minQty, unitPriceMinor: l.unitPriceMinor, belowFloorReason: l.belowFloorReason }));
      let d = detail;
      const batches = chunkEntries(items);
      for (let i = 0; i < batches.length; i++) {
        try {
          d = (await priceListsApi.upsertEntries(detail.id, batches[i])).data;
        } catch (err) {
          if (i > 0) apply(d);
          const done = i * 500;
          return [priceListError(err).text + (done > 0 ? ` The first ${done} lines were saved; fix the problem and paste the rest.` : ' Nothing was added.')];
        }
      }
      apply(d);
      return [];
    } catch (err) {
      return [priceListError(err).text];
    } finally {
      setBusy(false);
    }
  };

  const saveHeader = async (body: { name: string; contractRef: string | null; validFromUtc: string; validToUtc: string }) => {
    const r = await run(() => priceListsApi.updateHeader(detail.id, body), 'Details saved.');
    if (r.ok) apply(r.value.data);
    return r.ok;
  };

  const submit = async () => {
    const r = await run(() => priceListsApi.submit(detail.id));
    setDialog(null);
    if (r.ok) { apply(r.value.data); setOkText('Submitted for approval.'); }
    else setRowErrors(entryFieldErrors(r.fields));
  };

  const revise = async () => {
    const r = await run(() => priceListsApi.revise(detail.id));
    if (r.ok) router.push(`/pricing/price-lists/${r.value.data.id}`);
  };

  const remove = async () => {
    const r = await run(() => priceListsApi.deleteDraft(detail.id));
    setDialog(null);
    if (r.ok) router.push('/pricing/price-lists');
  };

  const issues: string[] = [];
  if (entries.length === 0) issues.push('The list has no entries.');
  const missing = entriesNeedingReason(entries);
  if (missing.length > 0) issues.push(`Below-floor entries need a reason first: ${missing.map((e) => e.sku).join(', ')}.`);
  const dearer = breaksOutOfOrder(entries);
  if (dearer.length > 0) issues.push(`A larger quantity must never cost more per unit. Check: ${dearer.join(', ')}.`);

  const banner =
    detail.status === 'PendingApproval' ? 'This list is waiting for approval by someone else. It is read-only until it is approved or sent back.'
    : detail.status === 'Approved' ? 'This list is approved and cannot be changed. To change prices, revise it: that creates a new draft version.'
    : detail.status === 'Withdrawn' ? 'This list was withdrawn and cannot be changed. You can revise it to start a new draft version.'
    : null;

  return (
    <div className="space-y-6">
      <p><Link href="/pricing/price-lists" className="text-sm font-semibold text-brand-accent underline">All price lists</Link></p>

      <section aria-label="Summary" className="space-y-3 rounded-xl border border-surface-border bg-surface p-6">
        <div className="flex flex-wrap items-center gap-3">
          <h2 className="font-display text-lg font-bold" data-testid="list-name">{detail.name}</h2>
          <StateBadges status={detail.status} displayState={detail.displayState} />
          <span className="font-mono text-xs text-text-muted">version {detail.version}</span>
        </div>
        <dl className="grid grid-cols-1 gap-x-6 gap-y-1 text-sm sm:grid-cols-2">
          <div><dt className="inline text-text-muted">Scope: </dt><dd className="inline">{detail.scope}{companyName ? `, ${companyName}` : ''}</dd></div>
          {detail.contractRef && <div><dt className="inline text-text-muted">Contract: </dt><dd className="inline font-mono">{detail.contractRef}</dd></div>}
          <div><dt className="inline text-text-muted">Valid (UTC): </dt><dd className="inline font-mono">{fmtDate(detail.validFromUtc)} to {fmtDate(detail.validToUtc)}</dd></div>
          {detail.effectiveFromUtc && <div><dt className="inline text-text-muted">Effective from: </dt><dd className="inline font-mono" data-testid="effective-from">{detail.effectiveFromUtc.replace('T', ' ').slice(0, 19)} UTC</dd></div>}
          <div><dt className="inline text-text-muted">Contributors: </dt><dd className="inline text-xs" data-testid="contributors">{detail.contributors.length === 0 ? 'none' : detail.contributors.map((c) => actorName(detail, c)).join(', ')}</dd></div>
        </dl>
        {detail.contentHash && <p className="text-xs text-text-muted">Content hash (SHA-256, fixed at submit): <span data-testid="content-hash" className="select-all break-all font-mono">{detail.contentHash}</span></p>}
        {detail.rejectionReason && <p className="text-sm text-fault">Sent back: {detail.rejectionReason}</p>}
        {detail.withdrawReason && <p className="text-sm text-fault">Withdrawn: {detail.withdrawReason}</p>}
      </section>

      {user && detail.status !== 'Draft' && (
        <PriceListDecisionPanel
          detail={detail}
          me={{ id: user.id, permissions: user.permissions }}
          onDecided={(d, message) => { apply(d); setOkText(message); }}
          onReload={res.reload}
        />
      )}
      {banner && <div role="status" data-testid="readonly-banner" className="rounded-md border border-surface-border bg-bg-subtle p-3 text-sm font-medium">{banner}</div>}
      {notice && (
        <div role="alert" className="flex flex-wrap items-center gap-3 rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">
          <span>{notice.text}</span>
          {notice.retry && <Button type="button" variant="outline" size="sm" onClick={res.reload}>Reload</Button>}
        </div>
      )}
      {okText && <p role="status" className="text-sm font-medium text-success">{okText}</p>}

      {editable && (
        <section aria-label="Details" className="space-y-3 rounded-xl border border-surface-border bg-surface p-6">
          <h2 className="font-display text-lg font-bold">Details</h2>
          <HeaderForm key={`${detail.name}|${detail.contractRef}|${detail.validFromUtc}|${detail.validToUtc}`} detail={detail} busy={busy} onSave={saveHeader} />
        </section>
      )}

      <section aria-label="Entries" className="space-y-4 rounded-xl border border-surface-border bg-surface p-6">
        <h2 className="font-display text-lg font-bold">Entries ({entries.length})</h2>
        {!showCost && entries.length > 0 && <p className="text-xs text-text-muted">Cost and margin-floor figures are shown only to roles with the cost permission.</p>}
        {entries.length === 0 ? (
          <p data-testid="no-entries" className="text-sm text-text-muted">No entries yet.{editable ? ' Add them below.' : ''}</p>
        ) : (
          <div className="overflow-x-auto rounded-md border border-surface-border">
            <table className="w-full text-left text-sm" data-testid="entries-table">
              <caption className="sr-only">Entries of {detail.name}</caption>
              <thead className="bg-bg-subtle text-xs text-text-muted">
                <tr>
                  <th scope="col" className="px-3 py-2">SKU / product</th>
                  <th scope="col" className="px-3 py-2">Min qty</th>
                  <th scope="col" className="px-3 py-2">Price (BDT)</th>
                  {showCost && <><th scope="col" className="px-3 py-2">Cost</th><th scope="col" className="px-3 py-2">Floor</th><th scope="col" className="px-3 py-2">Flags</th></>}
                  <th scope="col" className="px-3 py-2">Below-floor reason</th>
                  {editable && <th scope="col" className="px-3 py-2"><span className="sr-only">Actions</span></th>}
                </tr>
              </thead>
              <tbody>
                {entries.map((e) => (
                  <EntryRow key={`${e.id}|${e.unitPriceMinor}|${e.minQty}|${e.belowFloorReason ?? ''}`} entry={e} scope={detail.scope} editable={editable} showCost={showCost} flagged={flaggedIds.has(e.id)} busy={busy} serverError={rowErrors[e.id]} others={entries} onSave={saveRow} onRemove={removeRow} />
                ))}
              </tbody>
            </table>
          </div>
        )}
        {editable && (
          <div className="space-y-5 border-t border-surface-border pt-4">
            <AddEntry scope={detail.scope} busy={busy} onAdd={addOne} />
            <BulkPaste scope={detail.scope} busy={busy} onAdd={addMany} />
          </div>
        )}
      </section>

      {canEdit && (
        <div className="flex flex-wrap gap-3">
          {editable && <Button type="button" onClick={() => setDialog('submit')} disabled={busy}>Submit for approval</Button>}
          {editable && <Button type="button" variant="destructive" onClick={() => setDialog('delete')} disabled={busy}>Delete draft</Button>}
          {canRevise(detail.status) && <Button type="button" onClick={revise} isLoading={busy} disabled={busy}>Revise (new draft version)</Button>}
        </div>
      )}

      <Modal open={dialog === 'submit'} onOpenChange={(next) => { if (!next && !busy) setDialog(null); }} title="Submit this list for approval?" description={<>
              {entries.length} entries will be locked and sent to an approver. Someone else must approve it (four eyes): you and everyone who edited the list are excluded.
              While it waits, nobody can edit it. If it is sent back, it returns to draft.
            </>}>
          {issues.length > 0 && <ul role="alert" className="list-disc space-y-1 pl-5 text-sm font-medium text-fault" data-testid="submit-issues">{issues.map((i) => <li key={i}>{i}</li>)}</ul>}
          <ModalFooter>
            <Button type="button" variant="outline" onClick={() => setDialog(null)} disabled={busy}>Cancel</Button>
            <Button type="button" onClick={submit} isLoading={busy} disabled={busy || issues.length > 0}>Confirm submit</Button>
          </ModalFooter>
      </Modal>

      <Modal open={dialog === 'delete'} onOpenChange={(next) => { if (!next && !busy) setDialog(null); }} title="Delete this draft?" description={<>The draft and its {entries.length} entries are removed for good. Approved lists are never affected.</>}>
          <ModalFooter>
            <Button type="button" variant="outline" onClick={() => setDialog(null)} disabled={busy}>Cancel</Button>
            <Button type="button" variant="destructive" onClick={remove} isLoading={busy} disabled={busy}>Delete draft</Button>
          </ModalFooter>
      </Modal>
    </div>
  );
}

export function PriceListEditorView({ id }: { id: string }) {
  const { user } = useAuth();
  const held = new Set((user?.permissions ?? []).map((p) => p.toLowerCase()));
  return (
    <ConsoleShell activeItemId="price-lists" title="Price list">
      {held.has('pricelists.view') ? (
        <Editor id={id} canEdit={held.has('pricelists.edit')} />
      ) : (
        <p role="alert" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">
          Your role does not allow this action (pricelists.view).
        </p>
      )}
    </ConsoleShell>
  );
}
