'use client';

import * as React from 'react';
import Link from 'next/link';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import { Textarea } from '@mechware/ui/src/components/Textarea';
import {
  CREDIT_METHODS, CREDIT_METHOD_ORDER, buildRecordBody, bdtToPoisha, canRecordCredit, dhakaToday, emptyRecordForm, formatBdt, isLateEntry, methodFields,
  newIdempotencyKey, recordAttemptFor, recordFormProblems, type CreditPayment, type CreditPaymentMethod, type RecordAttempt, type RecordPaymentForm
} from '@mechware/api-client';
import { creditError, creditPaymentsApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { CompanyPicker } from './CompanyPicker';
import { ConsoleShell } from './ConsoleShell';

const SERVER_FIELD: Record<string, string> = { amountMinor: 'amountBdt', receivedOn: 'receivedOn', reference: 'reference', drawerBank: 'drawerBank', chequeDate: 'chequeDate', lateEntryReason: 'lateEntryReason', note: 'note', method: 'method' };

function RecordForm() {
  const [today, setToday] = React.useState(() => dhakaToday());
  const [form, setForm] = React.useState<RecordPaymentForm>(() => emptyRecordForm(dhakaToday()));
  const [company, setCompany] = React.useState<{ id: string; name: string }>({ id: '', name: '' });
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [done, setDone] = React.useState<{ payment: CreditPayment; companyName: string; replayed: boolean } | null>(null);
  // One Idempotency-Key per form submit: kept for a retry of the SAME details, replaced when anything changed.
  const attempt = React.useRef<RecordAttempt | null>(null);
  const alertRef = React.useRef<HTMLDivElement>(null);

  React.useEffect(() => { setToday(dhakaToday()); }, []);
  React.useEffect(() => { if (alertText) alertRef.current?.focus(); }, [alertText]);

  const patch = (p: Partial<RecordPaymentForm>) => { setForm((f) => ({ ...f, ...p })); setErrors((e) => { const n = { ...e }; for (const k of Object.keys(p)) delete n[k]; return n; }); };
  const fields = methodFields(form.method);
  const minor = bdtToPoisha(form.amountBdt);
  const late = isLateEntry(form.receivedOn, today);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (busy) return;
    const problems = recordFormProblems(form, today);
    if (!company.id) problems.company = 'Choose the company that paid.';
    setErrors(problems);
    if (Object.keys(problems).length > 0) { setAlertText('Check the highlighted boxes.'); return; }
    const body = buildRecordBody(form);
    attempt.current = recordAttemptFor(attempt.current, company.id, body, newIdempotencyKey);
    setBusy(true);
    setAlertText(null);
    try {
      const res = await creditPaymentsApi.record(company.id, body, attempt.current.key);
      setDone({ payment: res.data, companyName: company.name, replayed: false });
      attempt.current = null;
    } catch (err) {
      const f = creditError(err);
      const mapped: Record<string, string> = {};
      for (const [k, v] of Object.entries(f.fields)) mapped[SERVER_FIELD[k] ?? k] = v;
      setErrors(mapped);
      setAlertText(f.text);
    } finally {
      setBusy(false);
    }
  };

  if (done) {
    const p = done.payment;
    return (
      <section aria-label="Payment recorded" className="space-y-3 rounded-xl border border-surface-border bg-surface p-6" data-testid="recorded-panel">
        <h2 className="font-display text-lg font-bold">Payment recorded</h2>
        <p role="status" className="text-sm">
          {CREDIT_METHODS[p.method as CreditPaymentMethod]?.label ?? p.method} {p.reference} of BDT {formatBdt(p.amountMinor)} from {done.companyName} is waiting for verification.
          It does not change the company&apos;s credit until a different person verifies it.
        </p>
        <div className="flex flex-wrap gap-3 text-sm">
          <Link className="font-semibold text-brand-accent underline" href={`/credit/payments/${p.id}`}>Open this payment</Link>
          <Button type="button" variant="outline" size="sm" onClick={() => { setDone(null); setForm(emptyRecordForm(dhakaToday())); setCompany({ id: '', name: '' }); }}>Record another payment</Button>
        </div>
      </section>
    );
  }

  return (
    <form onSubmit={submit} noValidate aria-label="Record a customer payment" className="space-y-5 rounded-xl border border-surface-border bg-surface p-4 sm:p-6">
      <p className="text-sm text-text-muted">
        Enter a payment exactly as the bank or the receipt shows it. Recording does not release any credit: a different person must verify it.
      </p>
      {alertText && <div ref={alertRef} tabIndex={-1} role="alert" data-testid="record-alert" className="rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">{alertText}</div>}

      <CompanyPicker value={company.id} onChange={(id, name) => { setCompany({ id, name }); setErrors((er) => { const n = { ...er }; delete n.company; return n; }); }} error={errors.company} />

      <div className="grid gap-4 sm:grid-cols-2">
        <label className="space-y-1 text-sm font-semibold">
          <span className="block">Method</span>
          <select
            value={form.method}
            onChange={(e) => patch({ method: e.target.value as CreditPaymentMethod })}
            className="block w-full rounded-md border border-surface-border bg-surface p-2.5 text-sm font-normal"
            data-testid="method"
          >
            {CREDIT_METHOD_ORDER.map((m) => <option key={m} value={m}>{CREDIT_METHODS[m].label}</option>)}
          </select>
        </label>
        <Input
          label="Amount (BDT)"
          value={form.amountBdt}
          onChange={(e) => patch({ amountBdt: e.target.value })}
          inputMode="decimal"
          autoComplete="off"
          error={errors.amountBdt}
          helperText={minor !== null && minor > 0 ? `${formatBdt(minor)} BDT (${minor.toLocaleString('en-US')} poisha)` : 'Taka with up to 2 decimals, for example 25,000.50.'}
        />
        <Input label="Date received" type="date" value={form.receivedOn} max={today} onChange={(e) => patch({ receivedOn: e.target.value })} error={errors.receivedOn} />
        {fields.reference && (
          <Input label={fields.referenceLabel} value={form.reference} onChange={(e) => patch({ reference: e.target.value })} maxLength={40} autoComplete="off" error={errors.reference} />
        )}
        {fields.chequeDate && <Input label="Date on the cheque" type="date" value={form.chequeDate} onChange={(e) => patch({ chequeDate: e.target.value })} error={errors.chequeDate} helperText="A later date makes it a post-dated cheque: it cannot be verified before that day." />}
        {fields.bank && <Input label="Bank" value={form.drawerBank} onChange={(e) => patch({ drawerBank: e.target.value })} maxLength={60} autoComplete="off" error={errors.drawerBank} />}
      </div>
      {form.method === 'cash' && <p className="text-sm text-text-muted">Cash gets a receipt number from the system; there is nothing to enter. Cash cannot bounce.</p>}
      {(late || form.lateEntryReason) && (
        <Textarea label="Why is this entered late? (5 to 500 characters)" value={form.lateEntryReason} onChange={(e) => patch({ lateEntryReason: e.target.value })} maxLength={500} rows={2} error={errors.lateEntryReason} />
      )}
      <Textarea label="Note for staff (optional)" value={form.note} onChange={(e) => patch({ note: e.target.value })} maxLength={500} rows={2} error={errors.note} />

      <div className="flex flex-wrap items-center gap-3">
        <Button type="submit" isLoading={busy} disabled={busy}>Record payment</Button>
        <Link href="/credit/payments" className="text-sm font-semibold text-brand-accent underline">Back to the queue</Link>
      </div>
    </form>
  );
}

export function CreditPaymentRecordView() {
  const { user } = useAuth();
  return (
    <ConsoleShell activeItemId="credit-record" title="Record a payment">
      {canRecordCredit(user?.permissions ?? []) ? (
        <RecordForm />
      ) : (
        <p role="alert" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">Your role does not allow this action (credit.record_payment).</p>
      )}
    </ConsoleShell>
  );
}
