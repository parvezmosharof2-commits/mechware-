'use client';

import * as React from 'react';
import { useTranslations } from 'next-intl';
import {
  Badge, Button, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, EmptyState, Input, Textarea, useToast
} from '@mechware/ui';
import {
  CompanyRoles, hasRole, parseAmountToMinor, shortId, validateDecision, validateRuleForm,
  type ApprovalRequestDto, type ApprovalRuleDto, type ApprovalStatus, type RuleFormError
} from '@mechware/api-client';
import { approvalsApi, companiesApi, problemCode, problemFieldErrors } from '../../lib/api';
import { useAuth } from '../../lib/auth';
import { useResource } from '../../lib/useResource';
import { ConfirmRemoveDialog, ListSkeleton, LoadError, useErrorText, useMoney } from './shared';

/** 'name.required' -> 'nameRequired' (next-intl keys cannot contain dots). */
const errorKey = (code: RuleFormError) => code.replace(/\.(\w)/, (_, c: string) => c.toUpperCase());

function useApprovalErrorText() {
  const base = useErrorText();
  const t = useTranslations('Company.approvals.errors');
  return React.useCallback(
    (err: unknown) => {
      const code = problemCode(err);
      if (code === 'APPROVAL_ALREADY_PENDING') return t('alreadyPending');
      if (code === 'NO_ELIGIBLE_APPROVER') return t('noApprover');
      return base(err);
    },
    [base, t]
  );
}

function StatusBadge({ status }: { status: ApprovalStatus }) {
  const t = useTranslations('Company.approvals.statusLabel');
  const variant = status === 'approved' ? 'success' : status === 'pending' ? 'low-stock' : status === 'rejected' ? 'deal' : 'outline';
  return <Badge variant={variant}>{t(status)}</Badge>;
}

function RequestCard({ request, children }: { request: ApprovalRequestDto; children?: React.ReactNode }) {
  const t = useTranslations('Company.approvals');
  const money = useMoney();
  const { user } = useAuth();
  const who = (id: string) => (id === user?.id ? `${shortId(id)} (${t('request.you')})` : shortId(id));
  return (
    <li className="rounded-lg border border-border-subtle bg-bg-surface p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0 text-sm">
          <div className="flex flex-wrap items-center gap-2 font-semibold">
            {t('request.subject')} · <span className="tabular-nums">{money({ minorUnits: request.amountMinor, currency: 'BDT' })}</span>
            <StatusBadge status={request.status} />
          </div>
          <div className="mt-1 text-xs text-fg-muted">
            {t('request.rule')}: {request.ruleName} · {t('request.requester')}: <span className="font-mono">{who(request.requesterUserId)}</span>
          </div>
        </div>
        {children && <div className="flex flex-wrap gap-2">{children}</div>}
      </div>
      <ol className="mt-3 space-y-1 text-xs" aria-label={t('request.chain')}>
        {request.steps.map((s) => (
          <li key={s.position} className="flex flex-wrap items-center gap-2">
            <span className="font-mono">{s.position + 1}. {who(s.approverUserId)}</span>
            <span className="text-fg-secondary">{t(`decision.${s.decision}`)}</span>
            {s.comment && <span className="text-fg-muted">&ldquo;{s.comment}&rdquo;</span>}
          </li>
        ))}
      </ol>
    </li>
  );
}

// ---------- decision ----------

function DecisionDialog({ companyId, target, onClose, onDone }: {
  companyId: string;
  target: { request: ApprovalRequestDto; approve: boolean } | null;
  onClose: () => void;
  onDone: () => void;
}) {
  const t = useTranslations('Company.approvals');
  const tc = useTranslations('Company.common');
  const errorText = useApprovalErrorText();
  const { toast } = useToast();
  const [comment, setComment] = React.useState('');
  const [fieldError, setFieldError] = React.useState<string | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  React.useEffect(() => { setComment(''); setFieldError(null); setError(null); }, [target]);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!target) return;
    const problem = validateDecision(target.approve, comment);
    if (problem) {
      setFieldError(problem === 'comment.required' ? t('actions.commentRequired') : t('actions.commentTooLong'));
      return;
    }
    setFieldError(null);
    setBusy(true);
    setError(null);
    try {
      await approvalsApi.decide(companyId, target.request.id, { approve: target.approve, comment: comment.trim() || null });
      toast({ title: target.approve ? t('actions.approved') : t('actions.rejected'), variant: 'success' });
      onDone();
      onClose();
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open={target !== null} onOpenChange={(next) => !next && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{target?.approve ? t('actions.confirmApprove') : t('actions.confirmReject')}</DialogTitle>
          <DialogDescription>{target?.request.ruleName}</DialogDescription>
        </DialogHeader>
        <form onSubmit={submit} noValidate className="space-y-3">
          {error && <div role="alert" className="rounded-md border border-state-danger bg-bg-elevated p-3 text-xs font-medium text-state-danger">{error}</div>}
          <Textarea label={t('actions.comment')} value={comment} onChange={(e) => setComment(e.target.value)} error={fieldError ?? undefined} rows={3} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={busy}>{tc('cancel')}</Button>
            <Button type="submit" variant={target?.approve ? 'primary' : 'destructive'} isLoading={busy}>
              {target?.approve ? t('actions.approve') : t('actions.reject')}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

// ---------- inbox ----------

function Inbox({ companyId, version, onChanged }: { companyId: string; version: number; onChanged: () => void }) {
  const t = useTranslations('Company.approvals');
  const list = useResource(async () => (await approvalsApi.listInbox(companyId)).data, `inbox:${companyId}:${version}`);
  const [target, setTarget] = React.useState<{ request: ApprovalRequestDto; approve: boolean } | null>(null);

  return (
    <section aria-labelledby="inbox-heading" className="space-y-3">
      <h2 id="inbox-heading" className="text-sm font-semibold">{t('inbox.title')}</h2>
      {list.loading ? <ListSkeleton rows={2} /> : list.error ? <LoadError error={list.error} onRetry={list.reload} /> : (list.data ?? []).length === 0 ? (
        <EmptyState title={t('inbox.empty')} description={t('inbox.emptyBody')} />
      ) : (
        <ul className="space-y-3">
          {(list.data ?? []).map((r) => (
            <RequestCard key={r.id} request={r}>
              <Button size="sm" onClick={() => setTarget({ request: r, approve: true })}>{t('actions.approve')}</Button>
              <Button size="sm" variant="outline" onClick={() => setTarget({ request: r, approve: false })}>{t('actions.reject')}</Button>
            </RequestCard>
          ))}
        </ul>
      )}
      <DecisionDialog companyId={companyId} target={target} onClose={() => setTarget(null)} onDone={() => { list.reload(); onChanged(); }} />
    </section>
  );
}

// ---------- my requests + submit ----------

function SubmitDialog({ companyId, open, onClose, onDone }: { companyId: string; open: boolean; onClose: () => void; onDone: () => void }) {
  const t = useTranslations('Company.approvals');
  const tc = useTranslations('Company');
  const errorText = useApprovalErrorText();
  const { toast } = useToast();
  const centres = useResource(async () => (await companiesApi.listCostCentres(companyId)).data, `cc-submit:${companyId}`, open);
  const [amount, setAmount] = React.useState('');
  const [costCentre, setCostCentre] = React.useState('');
  const [fieldError, setFieldError] = React.useState<string | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  React.useEffect(() => { if (open) { setAmount(''); setCostCentre(''); setFieldError(null); setError(null); } }, [open]);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    const minor = parseAmountToMinor(amount);
    if (minor === null || minor <= 0) {
      setFieldError(tc('errors.amountFormat'));
      return;
    }
    setFieldError(null);
    setBusy(true);
    setError(null);
    try {
      const res = await approvalsApi.submit(companyId, {
        subjectType: 'purchase_request',
        subjectId: crypto.randomUUID(),
        amountMinor: minor,
        costCentreId: costCentre || null
      });
      toast({ title: res.data.approvalRequired ? t('submit.created') : t('submit.notRequired'), variant: 'success' });
      onDone();
      onClose();
    } catch (err) {
      setFieldError(problemFieldErrors(err).amount ?? null);
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open={open} onOpenChange={(next) => !next && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{t('submit.title')}</DialogTitle>
          <DialogDescription>{t('submit.intro')}</DialogDescription>
        </DialogHeader>
        <form onSubmit={submit} noValidate className="space-y-3">
          {error && <div role="alert" className="rounded-md border border-state-danger bg-bg-elevated p-3 text-xs font-medium text-state-danger">{error}</div>}
          <Input label={t('submit.amount')} value={amount} onChange={(e) => setAmount(e.target.value)} error={fieldError ?? undefined} inputMode="decimal" />
          <div>
            <label htmlFor="submit-cc" className="block text-xs font-semibold text-fg-secondary">{t('submit.costCentre')}</label>
            <select id="submit-cc" value={costCentre} onChange={(e) => setCostCentre(e.target.value)}
              className="mt-1 w-full rounded-md border border-border-subtle bg-bg-canvas px-3 py-2 text-sm text-fg-primary">
              <option value="">{t('submit.none')}</option>
              {(centres.data ?? []).map((c) => <option key={c.id} value={c.id}>{c.code} · {c.name}</option>)}
            </select>
          </div>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={busy}>{tc('common.cancel')}</Button>
            <Button type="submit" isLoading={busy}>{t('submit.submit')}</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function MyRequests({ companyId, canSubmit, version, onChanged }: { companyId: string; canSubmit: boolean; version: number; onChanged: () => void }) {
  const t = useTranslations('Company.approvals');
  const tc = useTranslations('Company.common');
  const errorText = useApprovalErrorText();
  const { toast } = useToast();
  const list = useResource(async () => (await approvalsApi.listMine(companyId)).data, `mine:${companyId}:${version}`);
  const [creating, setCreating] = React.useState(false);
  const [cancelTarget, setCancelTarget] = React.useState<ApprovalRequestDto | null>(null);
  const [cancelError, setCancelError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);

  const cancel = async () => {
    if (!cancelTarget) return;
    setBusy(true);
    try {
      await approvalsApi.cancel(companyId, cancelTarget.id);
      toast({ title: t('actions.cancelled'), variant: 'success' });
      setCancelTarget(null);
      setCancelError(null);
      list.reload();
      onChanged();
    } catch (err) {
      setCancelError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section aria-labelledby="mine-heading" className="space-y-3">
      <div className="flex items-center justify-between gap-3">
        <h2 id="mine-heading" className="text-sm font-semibold">{t('mine.title')}</h2>
        {canSubmit && <Button size="sm" onClick={() => setCreating(true)}>{t('mine.new')}</Button>}
      </div>
      {list.loading ? <ListSkeleton rows={2} /> : list.error ? <LoadError error={list.error} onRetry={list.reload} /> : (list.data ?? []).length === 0 ? (
        <EmptyState title={t('mine.empty')} description={t('mine.emptyBody')} />
      ) : (
        <ul className="space-y-3">
          {(list.data ?? []).map((r) => (
            <RequestCard key={r.id} request={r}>
              {r.status === 'pending' && canSubmit && (
                <Button size="sm" variant="ghost" onClick={() => { setCancelError(null); setCancelTarget(r); }}>{t('actions.cancel')}</Button>
              )}
            </RequestCard>
          ))}
        </ul>
      )}
      <SubmitDialog companyId={companyId} open={creating} onClose={() => setCreating(false)} onDone={() => { list.reload(); onChanged(); }} />
      <ConfirmRemoveDialog open={cancelTarget !== null} busy={busy} onConfirm={cancel} onClose={() => setCancelTarget(null)} extra={cancelError ?? tc('confirmRemoveBody')} />
    </section>
  );
}

// ---------- rules (admin) ----------

type Member = { userId: string; roles: number; isActive: boolean };

function RuleDialog({ companyId, rule, approvers, onClose, onSaved }: {
  companyId: string;
  rule: ApprovalRuleDto | 'new' | null;
  approvers: Member[];
  onClose: () => void;
  onSaved: () => void;
}) {
  const t = useTranslations('Company.approvals');
  const tc = useTranslations('Company.common');
  const errorText = useApprovalErrorText();
  const { toast } = useToast();
  const { user } = useAuth();
  const editing = rule && rule !== 'new' ? rule : null;
  const centres = useResource(async () => (await companiesApi.listCostCentres(companyId)).data, `cc-rule:${companyId}`, rule !== null);
  const [name, setName] = React.useState('');
  const [minAmount, setMinAmount] = React.useState('');
  const [costCentre, setCostCentre] = React.useState('');
  const [chain, setChain] = React.useState<string[]>([]);
  const [pick, setPick] = React.useState('');
  const [isActive, setIsActive] = React.useState(true);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [formError, setFormError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);

  React.useEffect(() => {
    if (rule === null) return;
    setErrors({});
    setFormError(null);
    setPick('');
    setName(editing?.name ?? '');
    setMinAmount(editing && editing.minAmountMinor > 0 ? String(editing.minAmountMinor / 100) : '');
    setCostCentre(editing?.costCentreId ?? '');
    setChain(editing?.approverUserIds ?? []);
    setIsActive(editing?.isActive ?? true);
  }, [rule, editing]);

  const available = approvers.filter((m) => !chain.includes(m.userId));
  const move = (i: number, d: -1 | 1) => setChain((c) => { const n = [...c]; const j = i + d; if (j < 0 || j >= n.length) return c; [n[i], n[j]] = [n[j], n[i]]; return n; });
  const label = (id: string) => (id === user?.id ? `${shortId(id)} (${t('request.you')})` : shortId(id));

  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    const found = validateRuleForm({ name, minAmountText: minAmount, approverUserIds: chain });
    if (Object.keys(found).length > 0) {
      const next: Record<string, string> = {};
      for (const [field, code] of Object.entries(found)) next[field] = t(`rules.errors.${errorKey(code as RuleFormError)}`);
      setErrors(next);
      return;
    }
    setErrors({});
    setBusy(true);
    setFormError(null);
    const body = {
      name: name.trim(),
      minAmountMinor: minAmount.trim() === '' ? 0 : (parseAmountToMinor(minAmount) ?? 0),
      costCentreId: costCentre || null,
      categoryIds: [],
      approverUserIds: chain,
      isActive
    };
    try {
      if (editing) await approvalsApi.updateRule(companyId, editing.id, body);
      else await approvalsApi.addRule(companyId, body);
      toast({ title: tc('saved'), variant: 'success' });
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
    <Dialog open={rule !== null} onOpenChange={(next) => !next && onClose()}>
      <DialogContent className="max-h-[90vh] overflow-y-auto">
        <DialogHeader><DialogTitle>{editing ? t('rules.edit') : t('rules.add')}</DialogTitle></DialogHeader>
        <form onSubmit={save} noValidate className="space-y-3">
          {formError && <div role="alert" className="rounded-md border border-state-danger bg-bg-elevated p-3 text-xs font-medium text-state-danger">{formError}</div>}
          <Input label={t('rules.name')} value={name} onChange={(e) => setName(e.target.value)} error={errors.name} required />
          <Input label={t('rules.minAmount')} helperText={t('rules.minAmountHint')} value={minAmount} onChange={(e) => setMinAmount(e.target.value)} error={errors.minAmount} inputMode="decimal" />
          <div>
            <label htmlFor="rule-cc" className="block text-xs font-semibold text-fg-secondary">{t('rules.costCentre')}</label>
            <select id="rule-cc" value={costCentre} onChange={(e) => setCostCentre(e.target.value)}
              className="mt-1 w-full rounded-md border border-border-subtle bg-bg-canvas px-3 py-2 text-sm text-fg-primary">
              <option value="">{t('rules.anyCostCentre')}</option>
              {(centres.data ?? []).map((c) => <option key={c.id} value={c.id}>{c.code} · {c.name}</option>)}
            </select>
          </div>

          <fieldset className="space-y-2">
            <legend className="text-xs font-semibold text-fg-secondary">{t('rules.chain')}</legend>
            <p className="text-xs text-fg-muted">{t('rules.chainHint')}</p>
            {approvers.length === 0 && <p className="text-xs text-state-danger">{t('rules.noApprovers')}</p>}
            <ol className="space-y-1">
              {chain.map((id, i) => (
                <li key={id} className="flex items-center justify-between gap-2 rounded-md border border-border-subtle bg-bg-surface px-3 py-2 text-xs">
                  <span className="font-mono">{i + 1}. {label(id)}</span>
                  <span className="flex gap-1">
                    <Button type="button" size="sm" variant="ghost" onClick={() => move(i, -1)} disabled={i === 0} aria-label={`${t('rules.moveUp')} ${i + 1}`}>↑</Button>
                    <Button type="button" size="sm" variant="ghost" onClick={() => move(i, 1)} disabled={i === chain.length - 1} aria-label={`${t('rules.moveDown')} ${i + 1}`}>↓</Button>
                    <Button type="button" size="sm" variant="ghost" onClick={() => setChain((c) => c.filter((x) => x !== id))} aria-label={`${t('rules.removeApprover')} ${i + 1}`}>✕</Button>
                  </span>
                </li>
              ))}
            </ol>
            {available.length > 0 && (
              <div className="flex gap-2">
                <select aria-label={t('rules.chooseApprover')} value={pick} onChange={(e) => setPick(e.target.value)}
                  className="min-w-0 flex-1 rounded-md border border-border-subtle bg-bg-canvas px-3 py-2 text-sm text-fg-primary">
                  <option value="">{t('rules.chooseApprover')}</option>
                  {available.map((m) => <option key={m.userId} value={m.userId}>{label(m.userId)}</option>)}
                </select>
                <Button type="button" variant="outline" disabled={!pick} onClick={() => { setChain((c) => [...c, pick]); setPick(''); }}>{t('rules.addApprover')}</Button>
              </div>
            )}
            {errors.approverUserIds && <p className="text-xs font-medium text-state-danger">{errors.approverUserIds}</p>}
          </fieldset>

          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={isActive} onChange={(e) => setIsActive(e.target.checked)} />
            {t('rules.active')}
          </label>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={busy}>{tc('cancel')}</Button>
            <Button type="submit" isLoading={busy}>{tc('save')}</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function Rules({ companyId, canWrite }: { companyId: string; canWrite: boolean }) {
  const t = useTranslations('Company.approvals');
  const tc = useTranslations('Company.common');
  const money = useMoney();
  const errorText = useApprovalErrorText();
  const { toast } = useToast();
  const rules = useResource(async () => (await approvalsApi.listRules(companyId)).data, `rules:${companyId}`);
  const members = useResource(async () => (await companiesApi.listMembers(companyId)).data, `rule-members:${companyId}`);
  const [dialog, setDialog] = React.useState<ApprovalRuleDto | 'new' | null>(null);
  const [removing, setRemoving] = React.useState<ApprovalRuleDto | null>(null);
  const [removeError, setRemoveError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);

  const approvers = (members.data ?? []).filter((m) => m.isActive && (hasRole(m.roles, CompanyRoles.Approver) || hasRole(m.roles, CompanyRoles.Admin)));

  const remove = async () => {
    if (!removing) return;
    setBusy(true);
    try {
      await approvalsApi.removeRule(companyId, removing.id);
      toast({ title: tc('removed'), variant: 'success' });
      setRemoving(null);
      setRemoveError(null);
      rules.reload();
    } catch (err) {
      setRemoveError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section aria-labelledby="rules-heading" className="space-y-3">
      <div className="flex items-center justify-between gap-3">
        <h2 id="rules-heading" className="text-sm font-semibold">{t('rules.title')}</h2>
        {canWrite && <Button size="sm" onClick={() => setDialog('new')}>{t('rules.add')}</Button>}
      </div>
      {rules.loading ? <ListSkeleton rows={2} /> : rules.error ? <LoadError error={rules.error} onRetry={rules.reload} /> : (rules.data ?? []).length === 0 ? (
        <EmptyState title={t('rules.empty')} description={t('rules.emptyBody')} />
      ) : (
        <ul className="space-y-3">
          {(rules.data ?? []).map((r) => (
            <li key={r.id} className="flex flex-wrap items-start justify-between gap-3 rounded-lg border border-border-subtle bg-bg-surface p-4">
              <div className="min-w-0 text-sm">
                <div className="flex flex-wrap items-center gap-2 font-semibold">
                  {r.name}
                  {!r.isActive && <Badge variant="outline">{t('rules.inactive')}</Badge>}
                </div>
                <div className="mt-1 text-xs text-fg-secondary tabular-nums">
                  {t('rules.appliesFrom')}: {r.minAmountMinor > 0 ? money({ minorUnits: r.minAmountMinor, currency: 'BDT' }) : t('rules.anyAmount')}
                  {r.costCentreId ? ` · ${t('rules.ctx')}` : ''}
                </div>
                <div className="mt-1 font-mono text-xs text-fg-muted">{r.approverUserIds.map((id, i) => `${i + 1}. ${shortId(id)}`).join('  →  ')}</div>
              </div>
              {canWrite && (
                <div className="flex gap-2">
                  <Button variant="outline" size="sm" onClick={() => setDialog(r)}>{tc('edit')}</Button>
                  <Button variant="ghost" size="sm" onClick={() => { setRemoveError(null); setRemoving(r); }}>{tc('remove')}</Button>
                </div>
              )}
            </li>
          ))}
        </ul>
      )}
      <RuleDialog companyId={companyId} rule={dialog} approvers={approvers} onClose={() => setDialog(null)} onSaved={rules.reload} />
      <ConfirmRemoveDialog open={removing !== null} busy={busy} onConfirm={remove} onClose={() => setRemoving(null)} extra={removeError} />
    </section>
  );
}

// ---------- tab ----------

export function ApprovalsPanel({ companyId, roles, suspended }: { companyId: string; roles: number; suspended: boolean }) {
  const t = useTranslations('Company.approvals');
  const isAdmin = hasRole(roles, CompanyRoles.Admin);
  const canApprove = isAdmin || hasRole(roles, CompanyRoles.Approver);
  const [version, setVersion] = React.useState(0);
  const bump = React.useCallback(() => setVersion((v) => v + 1), []);

  return (
    <div className="mt-4 space-y-6">
      <p className="text-xs text-fg-muted">{t('intro')}</p>
      {canApprove && <Inbox companyId={companyId} version={version} onChanged={bump} />}
      <MyRequests companyId={companyId} canSubmit={!suspended} version={version} onChanged={bump} />
      {isAdmin && <Rules companyId={companyId} canWrite={!suspended} />}
    </div>
  );
}

