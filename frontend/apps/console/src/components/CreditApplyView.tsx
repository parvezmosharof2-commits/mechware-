'use client';

import * as React from 'react';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import {
  MAX_APPLY_ALLOCATIONS, applyProblems, applyResultText, bodyAttemptFor, buildApplyBody, canRecordCredit, emptyApplyForm, formatBdt, newIdempotencyKey,
  type AppliedOnAccount, type ApplyForm, type BodyAttempt
} from '@mechware/api-client';
import { creditError } from '../lib/api';
import { creditBooksApi } from '../lib/creditBooks';
import { useAuth } from '../lib/auth';
import { useCompanyChoices } from '../lib/useCompanyChoices';
import { ConsoleShell } from './ConsoleShell';
import { BackLink, FailureAlert, FocusAlert, Loading, NoAccess, Notice, useFocusOn } from './CreditShared';

function ApplyScreen({ companyId }: { companyId: string }) {
  const choices = useCompanyChoices(companyId);
  const [form, setForm] = React.useState<ApplyForm>(emptyApplyForm());
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [done, setDone] = React.useState<AppliedOnAccount | null>(null);
  // One Idempotency-Key per distinct body: a retry of the same details keeps it, a changed form gets a new one.
  const attempt = React.useRef<BodyAttempt | null>(null);
  const alertRef = useFocusOn(alertText);

  const st = choices.statement;
  const onAccount = st ? st.currentOnAccountMinor : null;
  const setLine = (i: number, patch: Partial<ApplyForm['allocations'][number]>) => {
    setForm((f) => ({ ...f, allocations: f.allocations.map((a, j) => (j === i ? { ...a, ...patch } : a)) }));
    setErrors((e) => ({ ...e, [`allocation${i}`]: '' }));
  };

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (busy) return;
    const problems = applyProblems(form, onAccount);
    setErrors(problems);
    if (Object.keys(problems).length > 0) { setAlertText('Check the highlighted boxes.'); return; }
    const body = buildApplyBody(form);
    attempt.current = bodyAttemptFor(attempt.current, companyId, body, newIdempotencyKey);
    setBusy(true);
    setAlertText(null);
    try {
      const res = await creditBooksApi.applyOnAccount(companyId, body, attempt.current.key);
      setDone(res.data);
      attempt.current = null;
      setForm(emptyApplyForm());
      choices.reload();
    } catch (err) {
      setAlertText(creditError(err).text);
    } finally { setBusy(false); }
  };

  if (choices.loading && !st) return <Loading />;
  if (choices.error) return <FailureAlert failure={creditError(choices.error)} onRetry={choices.reload} />;
  if (!st) return null;

  return (
    <div className="space-y-5">
      <BackLink href={`/credit/companies/${companyId}`}>Back to {st.companyName}</BackLink>
      <section aria-label="Position" className="rounded-xl border border-surface-border bg-surface p-4 text-sm" data-testid="apply-position">
        <h2 className="font-display text-lg font-bold">{st.companyName}</h2>
        <p>On account (money paid before it was owed): <strong className="font-mono" data-testid="apply-on-account">BDT {formatBdt(st.currentOnAccountMinor)}</strong>. Owes now: <span className="font-mono">BDT {formatBdt(st.currentReceivableMinor)}</span>.</p>
      </section>
      {done && <Notice testId="apply-result">{applyResultText(done)}</Notice>}
      <form onSubmit={submit} noValidate aria-label="Apply on-account money" className="space-y-4 rounded-xl border border-surface-border bg-surface p-4 sm:p-6">
        <p className="text-sm text-text-muted">
          Puts the company&apos;s on-account money against what it owes, oldest money first. Leave the amount empty to apply everything that fits, oldest due first. Naming orders puts money on those orders first.
          No authenticator code is needed: the money was already verified by two people. Sending the same details twice applies them once.
        </p>
        {alertText && <FocusAlert ref={alertRef} testId="apply-alert">{alertText}</FocusAlert>}
        <Input
          label="Amount to apply (BDT, optional)"
          value={form.amountBdt}
          onChange={(e) => { setForm({ ...form, amountBdt: e.target.value }); setErrors((x) => ({ ...x, amountBdt: '' })); }}
          inputMode="decimal"
          autoComplete="off"
          error={errors.amountBdt || undefined}
          helperText="Empty = everything that fits."
        />
        <fieldset className="space-y-3">
          <legend className="text-sm font-semibold">Named orders (optional)</legend>
          {!choices.complete && <p className="text-xs text-text-muted">This company has a long ledger: orders are taken from its first and last 200 lines, and the amounts owed are not shown.</p>}
          {form.allocations.map((a, i) => (
            <div key={i} className="grid gap-3 sm:grid-cols-[1fr_12rem_auto] sm:items-end" data-testid="allocation-row">
              <label className="space-y-1 text-sm font-semibold">
                <span className="block">Order {i + 1}</span>
                <select
                  value={a.orderId}
                  onChange={(e) => setLine(i, { orderId: e.target.value })}
                  className="block w-full rounded-md border border-surface-border bg-surface p-2.5 text-sm font-normal"
                  aria-invalid={Boolean(errors[`allocation${i}`])}
                  aria-describedby={errors[`allocation${i}`] ? `alloc-err-${i}` : undefined}
                >
                  <option value="">Choose an order</option>
                  {choices.orders.map((o) => <option key={o.orderId} value={o.orderId}>{o.orderNumber}{choices.complete ? ` (owes BDT ${formatBdt(o.owedMinor)})` : ''}</option>)}
                </select>
              </label>
              <Input label={`Amount for order ${i + 1} (BDT)`} value={a.amountBdt} onChange={(e) => setLine(i, { amountBdt: e.target.value })} inputMode="decimal" autoComplete="off" />
              <Button type="button" variant="ghost" size="sm" onClick={() => setForm((f) => ({ ...f, allocations: f.allocations.filter((_, j) => j !== i) }))}>Remove order {i + 1}</Button>
              {errors[`allocation${i}`] && <p id={`alloc-err-${i}`} className="text-sm font-medium text-fault sm:col-span-3">{errors[`allocation${i}`]}</p>}
            </div>
          ))}
          {errors.allocations && <p className="text-sm font-medium text-fault">{errors.allocations}</p>}
          <Button type="button" variant="outline" size="sm" disabled={form.allocations.length >= MAX_APPLY_ALLOCATIONS || choices.orders.length === 0} onClick={() => setForm((f) => ({ ...f, allocations: [...f.allocations, { orderId: '', amountBdt: '' }] }))}>
            Name an order
          </Button>
          {choices.orders.length === 0 && <p className="text-xs text-text-muted">No order of this company owes anything.</p>}
        </fieldset>
        <Button type="submit" isLoading={busy} disabled={busy}>Apply on-account money</Button>
      </form>
    </div>
  );
}

export function CreditApplyView({ id }: { id: string }) {
  const { user } = useAuth();
  return (
    <ConsoleShell activeItemId="credit-statement" title="Apply on-account money">
      {canRecordCredit(user?.permissions ?? []) ? <ApplyScreen companyId={id} /> : <NoAccess permission="credit.record_payment" />}
    </ConsoleShell>
  );
}
