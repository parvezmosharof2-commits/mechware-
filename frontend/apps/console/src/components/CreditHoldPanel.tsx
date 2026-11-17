'use client';

import * as React from 'react';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import { Textarea } from '@mechware/ui/src/components/Textarea';
import { HOLD_MAX_DAYS, buildHoldBody, holdFormProblems, holdResultText, holdText, isCreditStepUpFailure, type HoldForm } from '@mechware/api-client';
import { creditError } from '../lib/api';
import { creditBooksApi } from '../lib/creditBooks';
import { FocusAlert, Notice, useFocusOn } from './CreditShared';
import { StepUpDialog } from './StepUpDialog';

/**
 * The overdue hold of one company and, for an Admin, the lift (credit.hold_override): 1 to 7 days, or 0 to end a lift, always with a
 * reason and an authenticator code. Days and reason are checked BEFORE the code is asked for, because a code is spent even when refused.
 */
export function CreditHoldPanel({ companyId, companyName, onHold, overrideUntilUtc, canOverride, onChanged }: {
  companyId: string; companyName: string; onHold: boolean; overrideUntilUtc: string | null; canOverride: boolean; onChanged: () => void;
}) {
  const [form, setForm] = React.useState<HoldForm>({ days: '', reason: '' });
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [pending, setPending] = React.useState<{ days: number; reason: string } | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [codeError, setCodeError] = React.useState<string | null>(null);
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const [notice, setNotice] = React.useState<string | null>(null);
  const alertRef = useFocusOn(alertText);

  const next = (e: React.FormEvent) => {
    e.preventDefault();
    const problems = holdFormProblems(form);
    setErrors(problems);
    if (Object.keys(problems).length > 0) return;
    setNotice(null); setAlertText(null); setCodeError(null);
    setPending(buildHoldBody(form));
  };

  const send = async (code: string) => {
    if (!pending) return;
    setBusy(true);
    setCodeError(null);
    try {
      const res = await creditBooksApi.holdOverride(companyId, pending, code);
      setPending(null);
      setForm({ days: '', reason: '' });
      setNotice(holdResultText(res.data, pending.days));
      onChanged();
    } catch (err) {
      const f = creditError(err);
      if (isCreditStepUpFailure(f.code)) setCodeError(f.text);
      else { setPending(null); setAlertText(f.text); }
    } finally { setBusy(false); }
  };

  return (
    <section aria-label="Overdue hold" className="space-y-3 rounded-xl border border-surface-border bg-surface p-4" data-testid="hold-panel">
      <h2 className="font-display text-lg font-bold">Overdue hold</h2>
      <p className="text-sm" data-testid="hold-state"><strong>{holdText(onHold, overrideUntilUtc)}.</strong></p>
      <p className="text-xs text-text-muted">A company is held when an invoice is more than 15 days past its due date. Payments, adjustments and gateway-paid orders are never blocked. An Admin may lift the hold for up to {HOLD_MAX_DAYS} days at a time.</p>
      {notice && <Notice testId="hold-notice">{notice}</Notice>}
      {alertText && <FocusAlert ref={alertRef} testId="hold-alert">{alertText}</FocusAlert>}
      {canOverride ? (
        <form onSubmit={next} noValidate aria-label="Lift the hold" className="grid gap-3 sm:grid-cols-[8rem_1fr]">
          <Input label="Days" inputMode="numeric" value={form.days} maxLength={2} autoComplete="off" onChange={(e) => { setForm({ ...form, days: e.target.value }); setErrors((x) => ({ ...x, days: '' })); }} error={errors.days || undefined} helperText={`0 to ${HOLD_MAX_DAYS}; 0 ends a lift`} />
          <Textarea label="Reason (5 to 500 characters)" rows={2} maxLength={500} value={form.reason} onChange={(e) => { setForm({ ...form, reason: e.target.value }); setErrors((x) => ({ ...x, reason: '' })); }} error={errors.reason || undefined} />
          <div className="sm:col-span-2"><Button type="submit" variant="outline" data-testid="hold-continue">Continue to the authenticator code</Button></div>
        </form>
      ) : (
        <p className="text-sm text-text-muted" data-testid="hold-readonly">Only an Admin (credit.hold_override) can lift the hold.</p>
      )}
      <StepUpDialog
        open={pending !== null}
        title={pending?.days === 0 ? 'End the hold lift' : 'Lift the overdue hold'}
        description={pending ? `${companyName}: ${pending.days === 0 ? 'end the lift now' : `lift the hold for ${pending.days} day${pending.days === 1 ? '' : 's'}`}. Reason: ${pending.reason}` : ''}
        confirmLabel={pending?.days === 0 ? 'End the lift' : 'Lift the hold'}
        busy={busy}
        error={codeError}
        onCancel={() => { if (!busy) setPending(null); }}
        onSubmit={send}
      />
    </section>
  );
}
