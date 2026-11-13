'use client';

import * as React from 'react';
import { useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import { isBdMobile, type BdLocationItem, type UserAddress } from '@mechware/api-client';
import { api, problemFieldErrors } from '../../lib/api';

type Level = 'division' | 'district' | 'upazila';

/** A labelled native select: accessible by default and light on the first-load budget. */
function PlaceSelect({ id, label, value, items, locale, error, onChange }: {
  id: string; label: string; value: string; items: BdLocationItem[]; locale: string; error?: string; onChange: (id: string) => void;
}) {
  const t = useTranslations('Checkout');
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className="text-sm font-medium text-text">{label}<span className="ml-1 text-fault">*</span></label>
      <select
        id={id}
        value={value}
        required
        aria-invalid={error ? true : undefined}
        aria-describedby={error ? `${id}-error` : undefined}
        onChange={(e) => onChange(e.target.value)}
        className="h-11 w-full rounded-md border border-surface-border-strong bg-bg-muted px-3 text-sm text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
      >
        <option value="">{t('select')}</option>
        {items.map((i) => <option key={i.id} value={i.id}>{locale === 'bn' ? i.nameBn : i.nameEn}</option>)}
      </select>
      {error && <p id={`${id}-error`} className="text-xs text-fault">{error}</p>}
    </div>
  );
}

/** Adds a delivery address to the buyer's address book with Division, District and Upazila chosen from the server's list. */
export function AddressForm({ locale, onSaved, onCancel }: { locale: string; onSaved: (address: UserAddress) => void; onCancel: () => void }) {
  const t = useTranslations('Checkout');
  const [form, setForm] = React.useState({ label: '', recipientName: '', phone: '', line1: '', line2: '', postalCode: '', upazilaText: '' });
  const [ids, setIds] = React.useState({ division: '', district: '', upazila: '' });
  const [lists, setLists] = React.useState<Record<Level, BdLocationItem[]>>({ division: [], district: [], upazila: [] });
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [saving, setSaving] = React.useState(false);
  const [failed, setFailed] = React.useState(false);
  /** The district whose upazilas have been loaded, so an empty list is told apart from one still loading. */
  const [upazilasFor, setUpazilasFor] = React.useState('');

  const load = React.useCallback(async (level: Level, parentId?: string) => {
    try {
      const res = await api.getBdLocations(level, parentId);
      setLists((l) => ({ ...l, [level]: res.data }));
      if (level === 'upazila' && parentId) setUpazilasFor(parentId);
    } catch {
      setFailed(true);
    }
  }, []);
  React.useEffect(() => { void load('division'); }, [load]);

  const choose = (level: Level, id: string) => {
    if (level === 'division') {
      setIds({ division: id, district: '', upazila: '' });
      setLists((l) => ({ ...l, district: [], upazila: [] }));
      if (id) void load('district', id);
    } else if (level === 'district') {
      setIds((s) => ({ ...s, district: id, upazila: '' }));
      setUpazilasFor('');
      setLists((l) => ({ ...l, upazila: [] }));
      if (id) void load('upazila', id);
    } else {
      setIds((s) => ({ ...s, upazila: id }));
    }
  };
  const set = (key: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement>) => setForm((f) => ({ ...f, [key]: e.target.value }));
  const nameOf = (level: Level, id: string) => lists[level].find((i) => i.id === id)?.nameEn ?? '';
  const needsUpazilaText = ids.district !== '' && upazilasFor === ids.district && lists.upazila.length === 0;

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    const next: Record<string, string> = {};
    for (const k of ['label', 'recipientName', 'line1'] as const) if (!form[k].trim()) next[k] = t('required');
    if (!isBdMobile(form.phone)) next.phone = t('invalidPhone');
    if (!ids.division) next.division = t('required');
    if (!ids.district) next.district = t('required');
    if (needsUpazilaText ? !form.upazilaText.trim() : !ids.upazila) next.upazila = t('required');
    setErrors(next);
    if (Object.keys(next).length > 0) return;
    setSaving(true);
    try {
      const res = await api.createAddress({
        label: form.label.trim(), recipientName: form.recipientName.trim(), phone: form.phone.trim(), line1: form.line1.trim(),
        line2: form.line2.trim() || null, area: null, postalCode: form.postalCode.trim() || null,
        division: nameOf('division', ids.division), district: nameOf('district', ids.district),
        upazila: needsUpazilaText ? form.upazilaText.trim() : nameOf('upazila', ids.upazila),
        isDefaultShipping: false, isDefaultBilling: false
      });
      onSaved(res.data);
    } catch (error) {
      const fields = problemFieldErrors(error);
      setErrors(Object.keys(fields).length > 0 ? fields : { form: t('addressSaveFailed') });
      setSaving(false);
    }
  };

  return (
    <form onSubmit={submit} noValidate aria-labelledby="addr-form-title" className="space-y-4 rounded-lg border border-surface-border bg-bg-muted p-4">
      <h3 id="addr-form-title" className="font-display text-base font-bold text-text">{t('addAddress')}</h3>
      {failed && <p role="alert" className="text-sm text-fault">{t('locationsFailed')}</p>}
      {errors.form && <p role="alert" className="text-sm text-fault">{errors.form}</p>}
      <div className="grid gap-4 md:grid-cols-2">
        <Input label={t('fieldLabel')} value={form.label} onChange={set('label')} error={errors.label} required maxLength={80} />
        <Input label={t('fieldRecipient')} value={form.recipientName} onChange={set('recipientName')} error={errors.recipientName} required maxLength={120} />
        <Input label={t('fieldPhone')} helperText={t('phoneHint')} type="tel" inputMode="tel" autoComplete="tel" value={form.phone} onChange={set('phone')} error={errors.phone} required />
        <Input label={t('fieldLine1')} value={form.line1} onChange={set('line1')} error={errors.line1} required maxLength={200} />
        <Input label={t('fieldLine2')} value={form.line2} onChange={set('line2')} error={errors.line2} maxLength={200} />
        <Input label={t('fieldPostal')} inputMode="numeric" value={form.postalCode} onChange={set('postalCode')} error={errors.postalCode} maxLength={10} />
      </div>
      <div className="grid gap-4 md:grid-cols-3">
        <PlaceSelect id="addr-division" label={t('fieldDivision')} value={ids.division} items={lists.division} locale={locale} error={errors.division} onChange={(id) => choose('division', id)} />
        <PlaceSelect id="addr-district" label={t('fieldDistrict')} value={ids.district} items={lists.district} locale={locale} error={errors.district} onChange={(id) => choose('district', id)} />
        {needsUpazilaText ? (
          <Input label={t('fieldUpazila')} value={form.upazilaText} onChange={set('upazilaText')} error={errors.upazila} required maxLength={80} />
        ) : (
          <PlaceSelect id="addr-upazila" label={t('fieldUpazila')} value={ids.upazila} items={lists.upazila} locale={locale} error={errors.upazila} onChange={(id) => choose('upazila', id)} />
        )}
      </div>
      <div className="flex flex-wrap gap-3">
        <Button type="submit" variant="accent" isLoading={saving} disabled={saving}>{t('saveAddress')}</Button>
        <Button type="button" variant="ghost" onClick={onCancel} disabled={saving}>{t('cancel')}</Button>
      </div>
    </form>
  );
}
