'use client';

import * as React from 'react';
import { useRouter } from 'next/navigation';
import { Button } from '@mechware/ui/src/components/Button';
import { canViewCredit } from '@mechware/api-client';
import { useAuth } from '../lib/auth';
import { CompanyPicker } from './CompanyPicker';
import { ConsoleShell } from './ConsoleShell';
import { NoAccess } from './CreditShared';

/** Choose a company to open its credit books (statement, hold, apply on-account, adjustments). */
export function CreditCompaniesView() {
  const { user } = useAuth();
  const router = useRouter();
  const [company, setCompany] = React.useState({ id: '', name: '' });
  const [problem, setProblem] = React.useState<string | null>(null);
  return (
    <ConsoleShell activeItemId="credit-statement" title="Company credit books">
      {canViewCredit(user?.permissions ?? []) ? (
        <form
          aria-label="Choose a company"
          className="space-y-4 rounded-xl border border-surface-border bg-surface p-4 sm:p-6"
          onSubmit={(e) => { e.preventDefault(); if (!company.id) return setProblem('Choose a company first.'); router.push(`/credit/companies/${company.id}`); }}
        >
          <p className="text-sm text-text-muted">Open the credit books of one company: what it owes, its statement, the overdue hold, on-account money and adjustments. Companies with debts are also listed in the aging report.</p>
          <CompanyPicker value={company.id} onChange={(id, name) => { setCompany({ id, name }); setProblem(null); }} error={problem ?? undefined} />
          <Button type="submit">Open the books{company.name ? ` of ${company.name}` : ''}</Button>
        </form>
      ) : <NoAccess permission="credit.view" />}
    </ConsoleShell>
  );
}
