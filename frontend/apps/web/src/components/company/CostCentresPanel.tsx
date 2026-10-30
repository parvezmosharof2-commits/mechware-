'use client';

import * as React from 'react';
import { useTranslations } from 'next-intl';
import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, EmptyState, Input, useToast } from '@mechware/ui';
import { amountToMinor, type CostCentreDto } from '@mechware/api-client';
import { companiesApi, problemFieldErrors } from '../../lib/api';
import { useResource } from '../../lib/useResource';
import { ConfirmRemoveDialog, ListSkeleton, LoadError, useErrorText, useMoney } from './shared';

function CostCentreDialog({ companyId, item, onClose, onSaved }: { companyId: string; item: CostCentreDto | 'new' | null; onClose: () => void; onSaved: () => void }) {
  const t = useTranslations('Company');
  const errorText = useErrorText();
  const { toast } = useToast();
  const editing = item && item !== 'new' ? item : null;
  const [code, setCode] = React.useState('');
  const [name, setName] = React.useState('');
  const [budget, setBudget] = React.useState('');
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [formError, setFormError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);

  React.useEffect(() => {
    if (item === null) return;
    setErrors({});
    setFormError(null);
    setCode(editing?.code ?? '');
    setName(editing?.name ?? '');
    setBudget(editing?.monthlyBudgetMinor != null ? String(editing.monthlyBudgetMinor / 100) : '');
  }, [item, editing]);

  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    const next: Record<string, string> = {};
    if (!code.trim()) next.code = t('errors.required');
    if (!name.trim()) next.name = t('errors.required');
    const amount = budget.trim() === '' ? null : Number(budget);
    if (amount !== null && (!Number.isFinite(amount) || amount < 0)) next.monthlyBudget = t('errors.amountFormat');
    setErrors(next);
    if (Object.keys(next).length > 0) return;

    setBusy(true);
    setFormError(null);
    const body = { code: code.trim(), name: name.trim(), monthlyBudgetMinor: amount === null ? null : amountToMinor(amount) };
    try {
      if (editing) await companiesApi.updateCostCentre(companyId, editing.id, body);
      else await companiesApi.addCostCentre(companyId, body);
      toast({ title: t('common.saved'), variant: 'success' });
      onSaved();
      onClose();
    } catch (err) {
      setErrors(problemFieldErrors(err));
      setFormError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open={item !== null} onOpenChange={(next) => !next && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{editing ? t('costCentres.editTitle') : t('costCentres.addTitle')}</DialogTitle>
        </DialogHeader>
        <form onSubmit={save} noValidate className="space-y-3">
          {formError && (
            <div role="alert" className="rounded-md border border-state-danger bg-bg-elevated p-3 text-xs font-medium text-state-danger">
              {formError}
            </div>
          )}
          <Input label={t('costCentres.code')} value={code} onChange={(e) => setCode(e.target.value)} error={errors.code} className="font-mono uppercase" required />
          <Input label={t('costCentres.name')} value={name} onChange={(e) => setName(e.target.value)} error={errors.name} required />
          <Input label={t('costCentres.budget')} value={budget} onChange={(e) => setBudget(e.target.value)} error={errors.monthlyBudget} inputMode="decimal" />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={busy}>{t('common.cancel')}</Button>
            <Button type="submit" isLoading={busy}>{t('common.save')}</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

export function CostCentresPanel({ companyId, canWrite }: { companyId: string; canWrite: boolean }) {
  const t = useTranslations('Company');
  const money = useMoney();
  const errorText = useErrorText();
  const { toast } = useToast();
  const list = useResource(async () => (await companiesApi.listCostCentres(companyId)).data, `cc:${companyId}`);
  const [dialog, setDialog] = React.useState<CostCentreDto | 'new' | null>(null);
  const [removing, setRemoving] = React.useState<CostCentreDto | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [removeError, setRemoveError] = React.useState<string | null>(null);

  const confirmRemove = async () => {
    if (!removing) return;
    setBusy(true);
    try {
      await companiesApi.removeCostCentre(companyId, removing.id);
      toast({ title: t('common.removed'), variant: 'success' });
      setRemoving(null);
      setRemoveError(null);
      list.reload();
    } catch (err) {
      setRemoveError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section aria-labelledby="cc-heading" className="mt-4 space-y-3">
      <div className="flex items-center justify-between gap-3">
        <h2 id="cc-heading" className="text-sm font-semibold">{t('costCentres.title')}</h2>
        {canWrite && <Button size="sm" onClick={() => setDialog('new')}>{t('common.add')}</Button>}
      </div>
      {!canWrite && <p className="text-xs text-fg-muted">{t('common.adminOnly')}</p>}

      {list.loading ? (
        <ListSkeleton rows={2} />
      ) : list.error ? (
        <LoadError error={list.error} onRetry={list.reload} />
      ) : (list.data ?? []).length === 0 ? (
        <EmptyState title={t('costCentres.empty')} description={t('costCentres.emptyBody')} />
      ) : (
        <ul className="space-y-3">
          {(list.data ?? []).map((c) => (
            <li key={c.id} className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-border-subtle bg-bg-surface p-4">
              <div className="min-w-0 text-sm">
                <span className="font-mono font-semibold">{c.code}</span> <span className="text-fg-secondary">{c.name}</span>
                {c.monthlyBudgetMinor != null && (
                  <div className="mt-1 text-xs text-fg-muted tabular-nums">
                    {t('costCentres.budget').replace(/\s*\(.*\)/, '')}: {money({ minorUnits: c.monthlyBudgetMinor, currency: 'BDT' })}
                  </div>
                )}
              </div>
              {canWrite && (
                <div className="flex gap-2">
                  <Button variant="outline" size="sm" onClick={() => setDialog(c)}>{t('common.edit')}</Button>
                  <Button variant="ghost" size="sm" onClick={() => { setRemoveError(null); setRemoving(c); }}>{t('common.remove')}</Button>
                </div>
              )}
            </li>
          ))}
        </ul>
      )}

      <CostCentreDialog companyId={companyId} item={dialog} onClose={() => setDialog(null)} onSaved={list.reload} />
      <ConfirmRemoveDialog open={removing !== null} busy={busy} onConfirm={confirmRemove} onClose={() => setRemoving(null)} extra={removeError} />
    </section>
  );
}
