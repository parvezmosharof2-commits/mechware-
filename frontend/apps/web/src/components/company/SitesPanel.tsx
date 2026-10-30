'use client';

import * as React from 'react';
import { useTranslations } from 'next-intl';
import { Badge, Button, Checkbox, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, EmptyState, Input, useToast } from '@mechware/ui';
import type { SiteDto, SiteRequest } from '@mechware/api-client';
import { companiesApi, problemFieldErrors } from '../../lib/api';
import { useResource } from '../../lib/useResource';
import { ConfirmRemoveDialog, ListSkeleton, LoadError, useErrorText } from './shared';

const emptyForm: SiteRequest = {
  name: '', contactName: '', contactPhone: '', line1: '', line2: '', upazila: '', district: '', division: '', postalCode: '', isDefaultDelivery: false
};

function SiteDialog({ companyId, site, onClose, onSaved }: { companyId: string; site: SiteDto | 'new' | null; onClose: () => void; onSaved: () => void }) {
  const t = useTranslations('Company');
  const errorText = useErrorText();
  const { toast } = useToast();
  const editing = site && site !== 'new' ? site : null;
  const [form, setForm] = React.useState<SiteRequest>(emptyForm);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [formError, setFormError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);

  React.useEffect(() => {
    if (site === null) return;
    setErrors({});
    setFormError(null);
    setForm(editing ? { ...editing, line2: editing.line2 ?? '', postalCode: editing.postalCode ?? '' } : emptyForm);
  }, [site, editing]);

  const set = (key: keyof SiteRequest) => (e: React.ChangeEvent<HTMLInputElement>) => setForm((f) => ({ ...f, [key]: e.target.value }));

  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    const required: (keyof SiteRequest)[] = ['name', 'contactName', 'contactPhone', 'line1', 'upazila', 'district', 'division'];
    const missing: Record<string, string> = {};
    for (const key of required) if (!String(form[key] ?? '').trim()) missing[key] = t('errors.required');
    setErrors(missing);
    if (Object.keys(missing).length > 0) return;

    setBusy(true);
    setFormError(null);
    const body: SiteRequest = { ...form, line2: form.line2?.trim() || null, postalCode: form.postalCode?.trim() || null };
    try {
      if (editing) await companiesApi.updateSite(companyId, editing.id, body);
      else await companiesApi.addSite(companyId, body);
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
    <Dialog open={site !== null} onOpenChange={(next) => !next && onClose()}>
      <DialogContent className="max-h-[90vh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>{editing ? t('sites.editTitle') : t('sites.addTitle')}</DialogTitle>
        </DialogHeader>
        <form onSubmit={save} noValidate className="space-y-3">
          {formError && (
            <div role="alert" className="rounded-md border border-state-danger bg-bg-elevated p-3 text-xs font-medium text-state-danger">
              {formError}
            </div>
          )}
          <Input label={t('sites.name')} value={form.name} onChange={set('name')} error={errors.name} required />
          <Input label={t('sites.contactName')} value={form.contactName} onChange={set('contactName')} error={errors.contactName} required />
          <Input label={t('sites.contactPhone')} value={form.contactPhone} onChange={set('contactPhone')} error={errors.contactPhone} type="tel" required />
          <Input label={t('sites.line1')} value={form.line1} onChange={set('line1')} error={errors.line1} required />
          <Input label={t('sites.line2')} value={form.line2 ?? ''} onChange={set('line2')} error={errors.line2} />
          <div className="grid gap-3 sm:grid-cols-3">
            <Input label={t('sites.upazila')} value={form.upazila} onChange={set('upazila')} error={errors.upazila} required />
            <Input label={t('sites.district')} value={form.district} onChange={set('district')} error={errors.district} required />
            <Input label={t('sites.division')} value={form.division} onChange={set('division')} error={errors.division} required />
          </div>
          <Input label={t('sites.postalCode')} value={form.postalCode ?? ''} onChange={set('postalCode')} error={errors.postalCode} />
          <Checkbox
            label={t('sites.isDefault')}
            checked={form.isDefaultDelivery}
            disabled={Boolean(editing?.isDefaultDelivery)}
            onCheckedChange={(v) => setForm((f) => ({ ...f, isDefaultDelivery: v === true }))}
          />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={busy}>
              {t('common.cancel')}
            </Button>
            <Button type="submit" isLoading={busy}>
              {t('common.save')}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

export function SitesPanel({ companyId, canWrite }: { companyId: string; canWrite: boolean }) {
  const t = useTranslations('Company');
  const errorText = useErrorText();
  const { toast } = useToast();
  const sites = useResource(async () => (await companiesApi.listSites(companyId)).data, `sites:${companyId}`);
  const [dialog, setDialog] = React.useState<SiteDto | 'new' | null>(null);
  const [removing, setRemoving] = React.useState<SiteDto | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [removeError, setRemoveError] = React.useState<string | null>(null);

  const confirmRemove = async () => {
    if (!removing) return;
    setBusy(true);
    try {
      await companiesApi.removeSite(companyId, removing.id);
      toast({ title: t('common.removed'), variant: 'success' });
      setRemoving(null);
      setRemoveError(null);
      sites.reload();
    } catch (err) {
      setRemoveError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section aria-labelledby="sites-heading" className="mt-4 space-y-3">
      <div className="flex items-center justify-between gap-3">
        <h2 id="sites-heading" className="text-sm font-semibold">{t('sites.title')}</h2>
        {canWrite && <Button size="sm" onClick={() => setDialog('new')}>{t('common.add')}</Button>}
      </div>
      {!canWrite && <p className="text-xs text-fg-muted">{t('common.adminOnly')}</p>}

      {sites.loading ? (
        <ListSkeleton rows={2} />
      ) : sites.error ? (
        <LoadError error={sites.error} onRetry={sites.reload} />
      ) : (sites.data ?? []).length === 0 ? (
        <EmptyState title={t('sites.empty')} description={t('sites.emptyBody')} />
      ) : (
        <ul className="space-y-3">
          {(sites.data ?? []).map((s) => (
            <li key={s.id} className="flex flex-wrap items-start justify-between gap-3 rounded-lg border border-border-subtle bg-bg-surface p-4">
              <div className="min-w-0 text-sm">
                <div className="flex flex-wrap items-center gap-2 font-semibold">
                  {s.name}
                  {s.isDefaultDelivery && <Badge variant="outline">{t('common.default')}</Badge>}
                </div>
                <div className="mt-1 text-fg-secondary">
                  {s.line1}{s.line2 ? `, ${s.line2}` : ''}, {s.upazila}, {s.district}, {s.division}{s.postalCode ? ` ${s.postalCode}` : ''}
                </div>
                <div className="mt-1 text-xs text-fg-muted">{s.contactName} · {s.contactPhone}</div>
              </div>
              {canWrite && (
                <div className="flex gap-2">
                  <Button variant="outline" size="sm" onClick={() => setDialog(s)}>{t('common.edit')}</Button>
                  <Button variant="ghost" size="sm" onClick={() => { setRemoveError(null); setRemoving(s); }}>{t('common.remove')}</Button>
                </div>
              )}
            </li>
          ))}
        </ul>
      )}

      <SiteDialog companyId={companyId} site={dialog} onClose={() => setDialog(null)} onSaved={sites.reload} />
      <ConfirmRemoveDialog open={removing !== null} busy={busy} onConfirm={confirmRemove} onClose={() => setRemoving(null)} extra={removeError} />
    </section>
  );
}
