'use client';

import * as React from 'react';
import Link from 'next/link';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import { Textarea } from '@mechware/ui/src/components/Textarea';
import {
  ADJUSTMENT_KINDS, ADJUSTMENT_KIND_ORDER, adjustmentFormProblems, adjustmentSummary, bodyAttemptFor, buildAdjustmentBody, canAdjustCredit, emptyAdjustmentForm,
  formatBdt, newIdempotencyKey, type AdjustmentForm, type AdjustmentKind, type BodyAttempt, type CreditAdjustment
} from '@mechware/api-client';
import { creditError } from '../lib/api';
import { creditBooksApi } from '../lib/creditBooks';
import { useAuth } from '../lib/auth';
import { useCompanyChoices } from '../lib/useCompanyChoices';
import { ConsoleShell } from './ConsoleShell';
import { BackLink, FailureAlert, FocusAlert, Loading, NoAccess, useFocusOn } from './CreditShared';

const SERVER_FIELD: Record<string, string> = { amountMinor: 'amountBdt', reason: 'reason', orderId: 'orderId', paymentId: 'paymentId', creditNoteId: 'creditNoteId', payoutReference: 'payoutReference', kind: 'kind' };

function SelectBox({ label, value, onChange, error, children, testId }: { label: string; value: string; onChange: (v: string) => void; error?: string; children: React.ReactNode; testId?: string }) {
  const id = React.useId();
  return (
    <div className="space-y-1">
      <label htmlFor={id} className="block text-sm font-semibold">{label}</label>
      <select id={id} value={value} onChange={(e) => onChange(e.target.value)} data-testid={testId} aria-invalid={Boolean(error)} aria-describedby={error ? `${id}-err` : undefined} className="block w-full rounded-md border border-surface-border bg-surface p-2.5 text-sm font-normal">
        {children}
      </select>
      {error && <p id={`${id}-err`} className="text-sm font-medium text-fault">{error}</p>}
    </div>
  );
}

function RequestForm({ companyId }: { companyId: string }) {
  const choices = useCompanyChoices(companyId);
  const [form, setForm] = React.useState<AdjustmentForm>(emptyAdjustmentForm());
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [done, setDone] = React.useState<CreditAdjustment | null>(null);
  const attempt = React.useRef<BodyAttempt | null>(null);
  const alertRef = useFocusOn(alertText);

  const info = ADJUSTMENT_KINDS[form.kind];
  const patch = (p: Partial<AdjustmentForm>) => { setForm((f) => ({ ...f, ...p })); setErrors((e) => { const n = { ...e }; for (const k of Object.keys(p)) delete n[k]; return n; }); };

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (busy) return;
    const problems = adjustmentFormProblems(form);
    setErrors(problems);
    if (Object.keys(problems).length > 0) { setAlertText('Check the highlighted boxes.'); return; }
    const body = buildAdjustmentBody(form);
    attempt.current = bodyAttemptFor(attempt.current, companyId, body, newIdempotencyKey);
    setBusy(true);
    setAlertText(null);
    try {
      const res = await creditBooksApi.requestAdjustment(companyId, body, attempt.current.key);
      setDone(res.data);
      attempt.current = null;
    } catch (err) {
      const f = creditError(err);
      const mapped: Record<string, string> = {};
      for (const [k, v] of Object.entries(f.fields)) mapped[SERVER_FIELD[k] ?? k] = v;
      setErrors(mapped);
      setAlertText(f.text);
    } finally { setBusy(false); }
  };

  if (choices.loading && !choices.statement) return <Loading />;
  if (choices.error) return <FailureAlert failure={creditError(choices.error)} onRetry={choices.reload} />;
  const st = choices.statement;
  if (!st) return null;

  if (done) {
    return (
      <section aria-label="Adjustment requested" className="space-y-3 rounded-xl border border-surface-border bg-surface p-6" data-testid="adjust-requested">
        <h2 className="font-display text-lg font-bold">Adjustment requested</h2>
        <p role="status" className="text-sm">{adjustmentSummary(done)} for {st.companyName} is waiting for an Admin other than you. It moves no money until then.</p>
        <div className="flex flex-wrap gap-3 text-sm">
          <Link className="font-semibold text-brand-accent underline" href={`/credit/adjustments/${done.id}`}>Open this adjustment</Link>
          <Button type="button" variant="outline" size="sm" onClick={() => { setDone(null); setForm(emptyAdjustmentForm()); }}>Request another</Button>
        </div>
      </section>
    );
  }

  return (
    <div className="space-y-5">
      <BackLink href={`/credit/companies/${companyId}`}>Back to {st.companyName}</BackLink>
      <form onSubmit={submit} noValidate aria-label="Request an adjustment" className="space-y-4 rounded-xl border border-surface-border bg-surface p-4 sm:p-6">
        <h2 className="font-display text-lg font-bold">{st.companyName}</h2>
        <p className="text-sm text-text-muted">A request moves no money. An Admin other than you (and other than whoever recorded or verified the payment it touches) must approve it with an authenticator code.</p>
        {alertText && <FocusAlert ref={alertRef} testId="adjust-alert">{alertText}</FocusAlert>}
        <SelectBox label="Kind" value={form.kind} onChange={(v) => patch({ kind: v as AdjustmentKind, amountBdt: '', orderId: '', paymentId: '', creditNoteId: '', payoutReference: '' })} testId="adjust-kind">
          {ADJUSTMENT_KIND_ORDER.map((k) => <option key={k} value={k}>{ADJUSTMENT_KINDS[k].label}</option>)}
        </SelectBox>
        <p className="text-sm text-text-muted" data-testid="adjust-help">{info.help}</p>

        {info.order && (
          <SelectBox label="Order" value={form.orderId} onChange={(v) => patch({ orderId: v })} error={errors.orderId} testId="adjust-order">
            <option value="">Choose the order</option>
            {choices.orders.map((o) => <option key={o.orderId} value={o.orderId}>{o.orderNumber}{choices.complete ? ` (owes BDT ${formatBdt(o.owedMinor)})` : ''}</option>)}
          </SelectBox>
        )}
        {info.source && (
          <>
            <SelectBox label="Source of the on-account money" value={form.sourceType} onChange={(v) => patch({ sourceType: v as AdjustmentForm['sourceType'], paymentId: '', creditNoteId: '' })}>
              <option value="payment">A payment</option>
              <option value="credit_note">A credit note</option>
            </SelectBox>
            {form.sourceType === 'payment' ? (
              <SelectBox label="Payment" value={form.paymentId} onChange={(v) => patch({ paymentId: v })} error={errors.paymentId} testId="adjust-payment">
                <option value="">Choose the payment</option>
                {choices.payments.map((p) => <option key={p.paymentId} value={p.paymentId}>{p.label}</option>)}
              </SelectBox>
            ) : (
              <Input label="Credit note id" value={form.creditNoteId} onChange={(e) => patch({ creditNoteId: e.target.value })} autoComplete="off" error={errors.creditNoteId} />
            )}
          </>
        )}
        <Input
          label={info.signed ? 'Amount (BDT, minus takes money off)' : 'Amount (BDT)'}
          value={form.amountBdt}
          onChange={(e) => patch({ amountBdt: e.target.value })}
          inputMode="decimal"
          autoComplete="off"
          error={errors.amountBdt}
          helperText={info.signed ? 'For example -250 or 100.50.' : 'Taka with up to 2 decimals.'}
        />
        {form.kind === 'on_account_payout' && (
          <Input label="Bank transfer reference MECHWARE used" value={form.payoutReference} onChange={(e) => patch({ payoutReference: e.target.value })} maxLength={40} autoComplete="off" error={errors.payoutReference} />
        )}
        <Textarea label="Reason (10 to 500 characters)" value={form.reason} onChange={(e) => patch({ reason: e.target.value })} maxLength={500} rows={3} error={errors.reason} />
        <Button type="submit" isLoading={busy} disabled={busy}>Request adjustment</Button>
      </form>
    </div>
  );
}

export function CreditAdjustRequestView({ id }: { id: string }) {
  const { user } = useAuth();
  return (
    <ConsoleShell activeItemId="credit-adjustments" title="Request an adjustment">
      {canAdjustCredit(user?.permissions ?? []) ? <RequestForm companyId={id} /> : <NoAccess permission="credit.adjust" />}
    </ConsoleShell>
  );
}
