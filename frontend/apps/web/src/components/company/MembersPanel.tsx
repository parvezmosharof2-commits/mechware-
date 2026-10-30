'use client';

import * as React from 'react';
import { useTranslations } from 'next-intl';
import { Badge, Button, Checkbox, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, EmptyState, Input, useToast } from '@mechware/ui';
import { CompanyRoles, amountToMinor, isValidRoles, roleNames, type CompanyRoleName, type MemberDto } from '@mechware/api-client';
import { companiesApi, problemFieldErrors } from '../../lib/api';
import { useAuth } from '../../lib/auth';
import { useResource } from '../../lib/useResource';
import { ConfirmRemoveDialog, ListSkeleton, LoadError, useErrorText, useMoney } from './shared';

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const ROLE_ORDER: CompanyRoleName[] = ['Requester', 'Buyer', 'Approver', 'Admin'];

function MemberDialog({ companyId, item, costCentres, onClose, onSaved }: {
  companyId: string;
  item: MemberDto | 'new' | null;
  costCentres: { id: string; code: string; name: string }[];
  onClose: () => void;
  onSaved: () => void;
}) {
  const t = useTranslations('Company');
  const errorText = useErrorText();
  const { toast } = useToast();
  const editing = item && item !== 'new' ? item : null;
  const [userId, setUserId] = React.useState('');
  const [roles, setRoles] = React.useState(0);
  const [spend, setSpend] = React.useState('');
  const [ccIds, setCcIds] = React.useState<string[]>([]);
  const [isActive, setIsActive] = React.useState(true);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [formError, setFormError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);

  React.useEffect(() => {
    if (item === null) return;
    setErrors({});
    setFormError(null);
    setUserId(editing?.userId ?? '');
    setRoles(editing?.roles ?? CompanyRoles.Requester);
    setSpend(editing?.spendLimitMinor != null ? String(editing.spendLimitMinor / 100) : '');
    setCcIds(editing?.costCentreIds ?? []);
    setIsActive(editing?.isActive ?? true);
  }, [item, editing]);

  const toggleRole = (name: CompanyRoleName, on: boolean) => setRoles((r) => (on ? r | CompanyRoles[name] : r & ~CompanyRoles[name]));
  const toggleCc = (id: string, on: boolean) => setCcIds((ids) => (on ? [...ids, id] : ids.filter((x) => x !== id)));

  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    const next: Record<string, string> = {};
    if (!editing && !UUID.test(userId.trim())) next.userId = t('errors.userIdFormat');
    if (!isValidRoles(roles)) next.roles = t('errors.required');
    const amount = spend.trim() === '' ? null : Number(spend);
    if (amount !== null && (!Number.isFinite(amount) || amount < 0)) next.spendLimit = t('errors.amountFormat');
    setErrors(next);
    if (Object.keys(next).length > 0) return;

    setBusy(true);
    setFormError(null);
    const common = { roles, spendLimitMinor: amount === null ? null : amountToMinor(amount), costCentreIds: ccIds };
    try {
      if (editing) await companiesApi.updateMember(companyId, editing.id, { ...common, isActive });
      else await companiesApi.addMember(companyId, { ...common, userId: userId.trim() });
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
      <DialogContent className="max-h-[90vh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>{editing ? t('members.editTitle') : t('members.addTitle')}</DialogTitle>
        </DialogHeader>
        <form onSubmit={save} noValidate className="space-y-3">
          {formError && (
            <div role="alert" className="rounded-md border border-state-danger bg-bg-elevated p-3 text-xs font-medium text-state-danger">
              {formError}
            </div>
          )}
          <Input
            label={t('members.userId')}
            helperText={t('members.userIdHint')}
            value={userId}
            onChange={(e) => setUserId(e.target.value)}
            error={errors.userId}
            disabled={Boolean(editing)}
            className="font-mono"
          />
          <fieldset className="space-y-2">
            <legend className="text-xs font-semibold text-fg-secondary">{t('members.roles')}</legend>
            {ROLE_ORDER.map((name) => (
              <Checkbox key={name} label={t(`roles.${name}`)} checked={(roles & CompanyRoles[name]) !== 0} onCheckedChange={(v) => toggleRole(name, v === true)} />
            ))}
            {errors.roles && <p className="text-xs font-medium text-state-danger">{errors.roles}</p>}
          </fieldset>
          <Input label={t('members.spendLimit')} value={spend} onChange={(e) => setSpend(e.target.value)} error={errors.spendLimit} inputMode="decimal" />
          {costCentres.length > 0 && (
            <fieldset className="space-y-2">
              <legend className="text-xs font-semibold text-fg-secondary">{t('members.costCentres')}</legend>
              {costCentres.map((c) => (
                <Checkbox key={c.id} label={`${c.code} · ${c.name}`} checked={ccIds.includes(c.id)} onCheckedChange={(v) => toggleCc(c.id, v === true)} />
              ))}
            </fieldset>
          )}
          {editing && <Checkbox label={t('members.active')} checked={isActive} onCheckedChange={(v) => setIsActive(v === true)} />}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={busy}>{t('common.cancel')}</Button>
            <Button type="submit" isLoading={busy}>{t('common.save')}</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

export function MembersPanel({ companyId, canWrite }: { companyId: string; canWrite: boolean }) {
  const t = useTranslations('Company');
  const money = useMoney();
  const { user } = useAuth();
  const errorText = useErrorText();
  const { toast } = useToast();
  const members = useResource(async () => (await companiesApi.listMembers(companyId)).data, `members:${companyId}`);
  const costCentres = useResource(async () => (await companiesApi.listCostCentres(companyId)).data, `cc-for-members:${companyId}`);
  const [dialog, setDialog] = React.useState<MemberDto | 'new' | null>(null);
  const [removing, setRemoving] = React.useState<MemberDto | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [removeError, setRemoveError] = React.useState<string | null>(null);

  const confirmRemove = async () => {
    if (!removing) return;
    setBusy(true);
    try {
      await companiesApi.removeMember(companyId, removing.id);
      toast({ title: t('common.removed'), variant: 'success' });
      setRemoving(null);
      setRemoveError(null);
      members.reload();
    } catch (err) {
      setRemoveError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section aria-labelledby="members-heading" className="mt-4 space-y-3">
      <div className="flex items-center justify-between gap-3">
        <h2 id="members-heading" className="text-sm font-semibold">{t('members.title')}</h2>
        {canWrite && <Button size="sm" onClick={() => setDialog('new')}>{t('common.add')}</Button>}
      </div>

      {members.loading ? (
        <ListSkeleton rows={2} />
      ) : members.error ? (
        <LoadError error={members.error} onRetry={members.reload} />
      ) : (members.data ?? []).length === 0 ? (
        <EmptyState title={t('members.empty')} />
      ) : (
        <ul className="space-y-3">
          {(members.data ?? []).map((m) => (
            <li key={m.id} className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-border-subtle bg-bg-surface p-4">
              <div className="min-w-0 text-sm">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="break-all font-mono text-xs">{m.userId}</span>
                  {user?.id === m.userId && <Badge variant="outline">{t('members.you')}</Badge>}
                  {!m.isActive && <Badge variant="deal">{t('common.inactive')}</Badge>}
                </div>
                <div className="mt-1 text-fg-secondary">{roleNames(m.roles).map((r) => t(`roles.${r}`)).join(', ')}</div>
                {m.spendLimitMinor != null && (
                  <div className="mt-1 text-xs text-fg-muted tabular-nums">{money({ minorUnits: m.spendLimitMinor, currency: 'BDT' })}</div>
                )}
              </div>
              {canWrite && (
                <div className="flex gap-2">
                  <Button variant="outline" size="sm" onClick={() => setDialog(m)}>{t('common.edit')}</Button>
                  <Button variant="ghost" size="sm" onClick={() => { setRemoveError(null); setRemoving(m); }}>{t('common.remove')}</Button>
                </div>
              )}
            </li>
          ))}
        </ul>
      )}

      <MemberDialog companyId={companyId} item={dialog} costCentres={costCentres.data ?? []} onClose={() => setDialog(null)} onSaved={members.reload} />
      <ConfirmRemoveDialog open={removing !== null} busy={busy} onConfirm={confirmRemove} onClose={() => setRemoving(null)} extra={removeError} />
    </section>
  );
}
