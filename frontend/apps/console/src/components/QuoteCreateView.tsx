'use client';

import * as React from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { Button } from '@mechware/ui/src/components/Button';
import { Input } from '@mechware/ui/src/components/Input';
import { canEditQuotes, newQuoteProblems } from '@mechware/api-client';
import { deskError, staffQuotesApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { CompanyPicker } from './CompanyPicker';
import { ConsoleShell } from './ConsoleShell';
import { BackLink, FocusAlert, NoAccess, useFocusOn } from './CreditShared';

function Form() {
  const router = useRouter();
  const [companyId, setCompanyId] = React.useState('');
  const [companyName, setCompanyName] = React.useState('');
  const [contact, setContact] = React.useState('');
  const [problems, setProblems] = React.useState<Record<string, string>>({});
  const [busy, setBusy] = React.useState(false);
  const inFlight = React.useRef(false);
  const [alertText, setAlertText] = React.useState<string | null>(null);
  const alertRef = useFocusOn(alertText);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (inFlight.current) return;
    const p = newQuoteProblems({ companyId, contactUserId: contact });
    setProblems(p);
    setAlertText(null);
    if (Object.keys(p).length > 0) return;
    inFlight.current = true;
    setBusy(true);
    try {
      const r = await staffQuotesApi.createQuote({ companyId, contactUserId: contact });
      router.push(`/quotes/${r.data.quote.id}`);
    } catch (err) {
      const f = deskError(err);
      if (f.status === 400 && Object.keys(f.fields).length > 0) setProblems(f.fields);
      setAlertText(f.text);
      inFlight.current = false;
      setBusy(false);
    }
  };

  return (
    <form onSubmit={submit} noValidate className="max-w-2xl space-y-5">
      <BackLink href="/quotes">Back to the quotes</BackLink>
      <p className="text-sm text-text-muted">
        Start an empty quote for a company that did not send a request. To answer a request, open it in the <Link href="/quotes/rfqs" className="text-brand-accent underline">RFQ inbox</Link>:
        its lines are pre-filled there. You cannot make a quote for a company you belong to.
      </p>
      {alertText && <FocusAlert ref={alertRef} testId="quote-create-alert">{alertText}</FocusAlert>}
      <CompanyPicker value={companyId} onChange={(id, name) => { setCompanyId(id); setCompanyName(name); }} error={problems.companyId} />
      {companyName && <p className="text-sm" data-testid="chosen-company">Chosen company: <strong>{companyName}</strong></p>}
      <Input label="Buyer contact (user id)" value={contact} onChange={(e) => setContact(e.target.value)} error={problems.contactUserId} autoComplete="off" spellCheck={false}
        helperText="The person at the company who will receive the quote. They must be a current member with the Buyer or Admin role. The server checks it." />
      <Button type="submit" isLoading={busy} disabled={busy}>Create the quote</Button>
    </form>
  );
}

export function QuoteCreateView() {
  const { user } = useAuth();
  return (
    <ConsoleShell activeItemId="quotes-new" title="New quote for a company">
      {canEditQuotes(user?.permissions ?? []) ? <Form /> : <NoAccess permission="quotes.edit" />}
    </ConsoleShell>
  );
}
