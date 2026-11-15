'use client';

import * as React from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { Button, Input } from '@mechware/ui';
import { dateToUtcIso, validateHeader, type HeaderErrors, type PriceListScope } from '@mechware/api-client';
import { priceListError, priceListsApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { ConsoleShell } from './ConsoleShell';
import { CompanyPicker } from './CompanyPicker';

function CreateForm() {
  const router = useRouter();
  const [scope, setScope] = React.useState<PriceListScope>('Company');
  const [companyId, setCompanyId] = React.useState('');
  const [name, setName] = React.useState('');
  const [contractRef, setContractRef] = React.useState('');
  const [from, setFrom] = React.useState('');
  const [to, setTo] = React.useState('');
  const [errors, setErrors] = React.useState<HeaderErrors>({});
  const [failure, setFailure] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    const body = { scope, companyId: scope === 'Company' ? companyId || null : null, contractRef: scope === 'Company' ? contractRef.trim() : null, name: name.trim(), validFromUtc: dateToUtcIso(from), validToUtc: dateToUtcIso(to) };
    const found = validateHeader(body);
    setErrors(found);
    if (Object.keys(found).length > 0) return;
    setBusy(true);
    setFailure(null);
    try {
      const created = (await priceListsApi.create(body)).data;
      router.push(`/pricing/price-lists/${created.id}`);
    } catch (err) {
      setFailure(priceListError(err).text);
      setBusy(false);
    }
  };

  return (
    <form onSubmit={submit} noValidate className="max-w-2xl space-y-5 rounded-xl border border-surface-border bg-surface p-6" aria-label="New price list">
      <p className="text-sm text-text-muted">The new list starts as a draft. Prices are VAT-exclusive for company contracts. Group lists are not available yet.</p>
      <fieldset className="space-y-2">
        <legend className="text-sm font-semibold">Scope</legend>
        <label className="flex items-center gap-2 text-sm"><input type="radio" name="scope" checked={scope === 'Company'} onChange={() => setScope('Company')} /> Company contract (one company)</label>
        <label className="flex items-center gap-2 text-sm"><input type="radio" name="scope" checked={scope === 'General'} onChange={() => setScope('General')} /> General quantity breaks (everyone)</label>
      </fieldset>
      <Input label="Name" required value={name} onChange={(e) => setName(e.target.value)} error={errors.name} maxLength={200} />
      {scope === 'Company' && (
        <>
          <CompanyPicker value={companyId} onChange={(id) => setCompanyId(id)} error={errors.companyId} />
          <Input label="Contract reference" required value={contractRef} onChange={(e) => setContractRef(e.target.value)} error={errors.contractRef} maxLength={64} />
        </>
      )}
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <Input label="Valid from (UTC date)" type="date" required value={from} onChange={(e) => setFrom(e.target.value)} error={errors.validFromUtc} />
        <Input label="Valid to (UTC date, exclusive)" type="date" required value={to} onChange={(e) => setTo(e.target.value)} error={errors.validToUtc} helperText="At most 366 days after the start." />
      </div>
      {failure && <div role="alert" className="rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">{failure}</div>}
      <div className="flex gap-3">
        <Button type="submit" isLoading={busy} disabled={busy}>Create draft</Button>
        <Link href="/pricing/price-lists" className="inline-flex items-center rounded-md border border-surface-border px-4 py-2 text-sm font-semibold">Cancel</Link>
      </div>
    </form>
  );
}

export function PriceListCreateView() {
  const { user } = useAuth();
  const held = new Set((user?.permissions ?? []).map((p) => p.toLowerCase()));
  return (
    <ConsoleShell activeItemId="price-lists" title="New price list">
      {held.has('pricelists.edit') ? (
        <CreateForm />
      ) : (
        <p role="alert" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">
          Your role does not allow this action (pricelists.edit).
        </p>
      )}
    </ConsoleShell>
  );
}
