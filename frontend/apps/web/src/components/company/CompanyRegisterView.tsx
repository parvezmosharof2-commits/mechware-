'use client';

import * as React from 'react';
import { useRouter } from 'next/navigation';
import { useTranslations } from 'next-intl';
import { Button, Input, useToast } from '@mechware/ui';
import { toRegisterRequest, validateCompanyRegistration, type CompanyFieldError } from '@mechware/api-client';
import { companiesApi, problemFieldErrors } from '../../lib/api';
import { PageShell, RequireSignIn, useErrorText, useLocalePath } from './shared';

type Form = { legalName: string; tradeLicenceNo: string; bin: string; tin: string; industry: string };

/** 'legalName.length' → 'legalNameLength' (next-intl keys cannot contain dots). */
const errorKey = (code: CompanyFieldError) => code.replace(/\.(\w)/, (_, c: string) => c.toUpperCase());

function RegisterForm() {
  const t = useTranslations('Company');
  const router = useRouter();
  const toPath = useLocalePath();
  const errorText = useErrorText();
  const { toast } = useToast();
  const [form, setForm] = React.useState<Form>({ legalName: '', tradeLicenceNo: '', bin: '', tin: '', industry: '' });
  const [fieldErrors, setFieldErrors] = React.useState<Partial<Record<keyof Form, string>>>({});
  const [formError, setFormError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);

  const set = (key: keyof Form) => (e: React.ChangeEvent<HTMLInputElement>) => setForm((f) => ({ ...f, [key]: e.target.value }));

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setFormError(null);
    const local = validateCompanyRegistration(form);
    if (Object.keys(local).length > 0) {
      const next: Partial<Record<keyof Form, string>> = {};
      for (const [field, code] of Object.entries(local)) next[field as keyof Form] = t(`errors.${errorKey(code as CompanyFieldError)}`);
      setFieldErrors(next);
      return;
    }
    setFieldErrors({});
    setBusy(true);
    try {
      const created = await companiesApi.register(toRegisterRequest(form));
      toast({ title: t('register.done'), variant: 'success' });
      router.push(toPath(`/account/company/${created.data.id}`));
    } catch (err) {
      const serverFields = problemFieldErrors(err);
      if (Object.keys(serverFields).length > 0) setFieldErrors(serverFields as Partial<Record<keyof Form, string>>);
      setFormError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={submit} noValidate className="space-y-4 rounded-lg border border-border-subtle bg-bg-surface p-5">
      <p className="text-sm text-fg-secondary">{t('register.intro')}</p>
      {formError && (
        <div role="alert" className="rounded-md border border-state-danger bg-bg-elevated p-3 text-xs font-medium text-state-danger">
          {formError}
        </div>
      )}
      <Input label={t('register.legalName')} value={form.legalName} onChange={set('legalName')} error={fieldErrors.legalName} autoComplete="organization" required />
      <Input label={t('register.tradeLicenceNo')} value={form.tradeLicenceNo} onChange={set('tradeLicenceNo')} error={fieldErrors.tradeLicenceNo} required />
      <Input
        label={t('register.bin')}
        helperText={t('register.binHint')}
        value={form.bin}
        onChange={set('bin')}
        error={fieldErrors.bin}
        inputMode="numeric"
        className="font-mono"
        required
      />
      <Input label={t('register.tin')} value={form.tin} onChange={set('tin')} error={fieldErrors.tin} inputMode="numeric" className="font-mono" />
      <Input label={t('register.industry')} value={form.industry} onChange={set('industry')} error={fieldErrors.industry} />
      <Button type="submit" isLoading={busy}>
        {t('register.submit')}
      </Button>
    </form>
  );
}

export function CompanyRegisterView() {
  const t = useTranslations('Company');
  const toPath = useLocalePath();
  return (
    <PageShell title={t('register.title')} back={{ href: toPath('/account/company'), label: t('backToList') }}>
      <RequireSignIn>
        <RegisterForm />
      </RequireSignIn>
    </PageShell>
  );
}
