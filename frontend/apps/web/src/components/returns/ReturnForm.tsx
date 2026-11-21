'use client';

import * as React from 'react';
import Link from 'next/link';
import { useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import { Textarea } from '@mechware/ui/src/components/Textarea';
import {
  RETURN_NOTE_MAX, RETURN_REASONS, buildReturnBody, keepReturnKey, problemCode, problemStatus, returnErrorKey, returnFormProblems, type OrderLine, type ReturnFormProblem
} from '@mechware/api-client';
import { returnsApi } from '../../lib/returns-client';
import { ReturnAlert } from './ReturnParts';

const selectClass = 'block h-11 w-full rounded-md border border-surface-border-strong bg-bg-muted px-3 text-sm text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent';

/**
 * Choose lines and quantities (at most what can still be returned), a reason and a note (needed for "other"), then send.
 * The Idempotency-Key is kept for a retry after a network or 5xx error (same body), and replaced after any other answer.
 */
export function ReturnForm({ orderNumber, companyId, lines, max, prefix, onSent, onCancel }: {
  orderNumber: string; companyId: string | null; lines: OrderLine[]; max: Record<string, number>; prefix: string; onSent: () => void; onCancel: () => void;
}) {
  const t = useTranslations('Returns');
  const [quantities, setQuantities] = React.useState<Record<string, number>>({});
  const [reasonCode, setReasonCode] = React.useState('damaged');
  const [note, setNote] = React.useState('');
  const [problems, setProblems] = React.useState<ReturnFormProblem[]>([]);
  const [failure, setFailure] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [sentId, setSentId] = React.useState<string | null>(null);
  const attempt = React.useRef<{ key: string; body: string } | null>(null);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (busy) return;
    const form = { quantities, reasonCode, note };
    const found = returnFormProblems(form, max);
    setProblems(found);
    setFailure(null);
    if (found.length > 0) return;
    const body = buildReturnBody(form, companyId);
    const json = JSON.stringify(body);
    if (!attempt.current || attempt.current.body !== json) attempt.current = { key: crypto.randomUUID(), body: json };
    setBusy(true);
    try {
      const created = await returnsApi.open(orderNumber, body, attempt.current.key);
      attempt.current = null;
      setSentId(created.id);
      onSent();
    } catch (err) {
      const status = problemStatus(err);
      if (!keepReturnKey(status)) attempt.current = null;
      setFailure(returnErrorKey(status, problemCode(err)));
    } finally {
      setBusy(false);
    }
  };

  if (sentId) {
    return (
      <p role="status" data-testid="return-sent" className="rounded-md border border-status-online/50 bg-status-online/10 p-3 text-sm text-text">
        {t('form.sent')} <Link prefetch={false} href={`${prefix}/account/returns/${sentId}`} className="font-semibold text-accent underline">{t('viewReturn')}</Link>
      </p>
    );
  }
  return (
    <form onSubmit={submit} noValidate className="space-y-4" data-testid="return-form">
      {(problems.length > 0 || failure) && (
        <ReturnAlert testId="return-problems">
          <ul className="list-disc space-y-1 pl-5">
            {problems.map((p) => <li key={p}>{t(`form.problem.${p}`)}</li>)}
            {failure && <li>{t(`error.${failure}`)}</li>}
          </ul>
        </ReturnAlert>
      )}
      <fieldset className="space-y-3">
        <legend className="mb-1 text-sm font-semibold text-text">{t('form.legend')}</legend>
        {lines.map((l) => {
          const id = l.id ?? '';
          const left = max[id] ?? 0;
          return (
            <div key={id || l.variantId} className="flex flex-wrap items-end justify-between gap-3 rounded-md border border-surface-border p-3">
              <div className="min-w-0 flex-1">
                <p className="break-words font-medium text-text">{l.title}</p>
                <p className="text-sm text-text-muted"><span className="break-all font-mono">{l.sku}</span> · {t('form.ordered', { ordered: l.quantity, max: left })}</p>
              </div>
              <div className="w-28">
                <Input
                  type="number" inputMode="numeric" min={0} max={left} step={1} disabled={left === 0 || busy}
                  label={t('form.qty')} aria-label={t('form.qtyFor', { title: l.title })}
                  value={quantities[id] ?? 0} data-testid={`return-qty-${l.sku}`}
                  onChange={(e) => setQuantities((q) => ({ ...q, [id]: Number.parseInt(e.target.value || '0', 10) || 0 }))}
                />
              </div>
            </div>
          );
        })}
      </fieldset>
      <div className="space-y-1.5">
        <label htmlFor="return-reason" className="text-sm font-medium text-text">{t('form.reason')}</label>
        <select id="return-reason" className={selectClass} value={reasonCode} onChange={(e) => setReasonCode(e.target.value)} disabled={busy} data-testid="return-reason">
          {RETURN_REASONS.map((r) => <option key={r} value={r}>{t(`reason.${r}`)}</option>)}
        </select>
      </div>
      <Textarea label={t('form.note')} helperText={reasonCode === 'other' ? t('form.noteRequired') : t('form.noteOptional')} value={note} maxLength={RETURN_NOTE_MAX}
        onChange={(e) => setNote(e.target.value)} rows={3} disabled={busy} data-testid="return-note" />
      <div className="flex flex-wrap gap-3">
        <Button type="submit" isLoading={busy} disabled={busy} data-testid="return-submit">{busy ? t('form.sending') : t('form.submit')}</Button>
        <Button type="button" variant="outline" onClick={onCancel} disabled={busy}>{t('form.cancel')}</Button>
      </div>
    </form>
  );
}
