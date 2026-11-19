'use client';

import * as React from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import {
  RFQ_MAX_MESSAGE,
  RFQ_MAX_QUANTITY,
  RFQ_MAX_TEXT,
  attemptFor,
  classifyRfqError,
  issueMessageKey,
  rfqKeepKeyAfter,
  newIdempotencyKey,
  parseRequestQuery,
  rfqFingerprint,
  rfqInputProblem,
  rfqLineErrors,
  type CreateRfqInput,
  type PlaceAttempt,
  type RfqErrorKind,
  type RfqLineError
} from '@mechware/api-client';
import { companiesApi, problemMessage } from '../../lib/api';
import { buyerRfqApi } from '../../lib/rfq-client';
import { useQuoteAccess } from '../../lib/useQuoteAccess';
import { PageShell, RequireSignIn, useLocalePath, ListSkeleton } from '../company/shared';

const MAX_FILE_BYTES = 100 * 1024;

type Failure = { kind: RfqErrorKind | 'textEmpty' | 'textTooLarge' | 'variantMissing' | 'quantityInvalid' | 'quantityTooLarge' | 'messageTooLong' | 'neededByInvalid' | 'fileLarge' | 'fileWrong' | 'fileRead'; lines: RfqLineError[]; detail?: string };

interface Site { id: string; name: string }

function dhakaDay(plusDays = 0): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Dhaka' }).format(new Date(Date.now() + plusDays * 86_400_000));
}

function Form() {
  const t = useTranslations('Quotes');
  const tq = useTranslations('QuickOrder');
  const router = useRouter();
  const toPath = useLocalePath();
  const params = useSearchParams();
  const access = useQuoteAccess();
  const query = parseRequestQuery({ source: params.get('source'), variantId: params.get('variantId'), quantity: params.get('quantity'), label: params.get('label') });

  const [source, setSource] = React.useState<'cart' | 'text' | 'product'>(query.source === 'csv' ? 'text' : query.source);
  const [companyId, setCompanyId] = React.useState('');
  const [text, setText] = React.useState('');
  const [fromFile, setFromFile] = React.useState(false);
  const [qty, setQty] = React.useState(String(query.quantity));
  const [message, setMessage] = React.useState('');
  const [neededBy, setNeededBy] = React.useState('');
  const [siteId, setSiteId] = React.useState('');
  const [sites, setSites] = React.useState<Site[]>([]);
  const [busy, setBusy] = React.useState(false);
  const [failure, setFailure] = React.useState<Failure | null>(null);

  const attempt = React.useRef<PlaceAttempt | null>(null);
  const sending = React.useRef(false);
  const alertRef = React.useRef<HTMLDivElement>(null);
  const areaRef = React.useRef<HTMLTextAreaElement>(null);

  const companies = access.phase === 'ready' ? access.companies : [];
  const activeId = companyId || companies[0]?.company.id || '';
  const activeCompany = companies.find((c) => c.company.id === activeId)?.company;

  React.useEffect(() => {
    if (failure) alertRef.current?.focus();
  }, [failure]);

  // The delivery sites of the chosen company (optional; a failure just hides the choice).
  React.useEffect(() => {
    if (!activeId) return;
    let current = true;
    // The old company's sites must never stay selectable while the new company's load.
    setSiteId('');
    setSites([]);
    companiesApi.listSites(activeId).then(
      (r) => current && setSites(r.data.map((s) => ({ id: s.id, name: s.name }))),
      () => current && setSites([])
    );
    return () => {
      current = false;
    };
  }, [activeId]);

  if (access.phase === 'loading') return <ListSkeleton rows={3} />;
  if (access.phase === 'error') {
    return (
      <div role="alert" className="rounded-lg border border-fault/50 bg-fault/10 p-4 text-sm">
        {t('loadError')}
      </div>
    );
  }
  if (access.phase !== 'ready') {
    return (
      <div data-testid="quote-company-only" className="space-y-3 rounded-lg border border-surface-border bg-bg-muted p-5">
        <h2 className="text-lg font-semibold">{t('companyOnlyTitle')}</h2>
        <p className="text-sm text-text-muted">{t('companyOnlyNote')}</p>
        <Link prefetch={false} href={toPath('/account/company/register')} className="text-sm font-semibold text-accent hover:underline">
          {t('registerCompany')}
        </Link>
      </div>
    );
  }

  const input = (): CreateRfqInput => ({
    source: source === 'text' && fromFile ? 'csv' : source,
    ...(source === 'text' ? { text } : {}),
    ...(source === 'product' ? { variantId: query.variantId, quantity: Number(qty) } : {}),
    message,
    neededBy,
    shipToSiteId: siteId
  });

  const onFile = async (file: File | undefined) => {
    if (!file) return;
    if (!/\.(csv|txt)$/i.test(file.name) && !/^text\//.test(file.type)) return setFailure({ kind: 'fileWrong', lines: [] });
    if (file.size > MAX_FILE_BYTES) return setFailure({ kind: 'fileLarge', lines: [] });
    try {
      const content = await file.text();
      setFailure(null);
      setText(content);
      setFromFile(true);
      areaRef.current?.focus();
    } catch {
      setFailure({ kind: 'fileRead', lines: [] });
    }
  };

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (sending.current) return; // a double click is one request; the Idempotency-Key makes a retry one RFQ as well
    const body = input();
    const problem = rfqInputProblem(body);
    if (problem) return setFailure({ kind: problem, lines: [] });
    sending.current = true;
    setBusy(true);
    setFailure(null);
    attempt.current = attemptFor(attempt.current, rfqFingerprint(activeId, body), newIdempotencyKey);
    try {
      const created = await buyerRfqApi.create(activeId, body, attempt.current.key);
      router.push(`${toPath(`/account/company/${encodeURIComponent(activeId)}/rfqs/${encodeURIComponent(created.rfq.id)}`)}?created=1`);
    } catch (err) {
      const kind = classifyRfqError(err);
      if (!rfqKeepKeyAfter(kind)) attempt.current = null;
      setFailure({ kind, lines: kind === 'linesInvalid' || kind === 'qtyExceeds' || kind === 'invalid' ? rfqLineErrors(err) : [], detail: kind === 'invalid' ? problemMessage(err, '') : undefined });
      sending.current = false;
      setBusy(false);
    }
  };

  const lineText = (n: number) => text.split(/\r\n|\n|\r/)[n - 1] ?? '';
  const sourceOptions: { value: 'cart' | 'text' | 'product'; label: string }[] = [
    { value: 'cart', label: t('sourceCart') },
    { value: 'text', label: t('sourceText') },
    ...(query.variantId ? [{ value: 'product' as const, label: t('sourceProduct') }] : [])
  ];

  return (
    <form onSubmit={submit} noValidate aria-busy={busy} className="space-y-6" data-testid="rfq-form">
      {failure && (
        <div ref={alertRef} tabIndex={-1} role="alert" data-testid="rfq-error" data-kind={failure.kind} className="space-y-2 rounded-md border border-fault/40 bg-fault/10 px-4 py-3 text-sm text-text outline-none">
          <p className="font-semibold">{t(`error_${failure.kind}`)}</p>
          {failure.detail && <p data-testid="rfq-error-detail">{failure.detail}</p>}
          {failure.lines.length > 0 && (
            <ul className="space-y-1" data-testid="rfq-line-errors">
              {failure.lines.map((l) => (
                <li key={`${l.lineNo}-${l.code ?? ''}`}>
                  <span className="font-semibold">{t('lineN', { n: l.lineNo })}</span>
                  {lineText(l.lineNo) && <code className="mx-2 rounded bg-bg-muted px-1.5 py-0.5 font-mono text-xs">{lineText(l.lineNo)}</code>}
                  <span>{l.code ? tq(issueMessageKey(l.code) as 'issue_unknown') : l.message}</span>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}

      {companies.length > 1 && (
        <div>
          <label htmlFor="rfq-company" className="mb-1 block text-sm font-semibold">{t('company')}</label>
          <select id="rfq-company" value={activeId} onChange={(e) => setCompanyId(e.target.value)} className="h-11 w-full rounded-md border border-surface-border-strong bg-bg-muted px-3 text-sm">
            {companies.map((c) => <option key={c.company.id} value={c.company.id}>{c.company.legalName}</option>)}
          </select>
        </div>
      )}
      {companies.length === 1 && activeCompany && <p className="text-sm text-text-muted">{t('forCompany', { company: activeCompany.legalName })}</p>}
      {activeCompany && activeCompany.status !== 'active' && (
        <p data-testid="rfq-company-status" className="rounded-md border border-status-pending/50 bg-status-pending/10 px-3 py-2 text-sm">
          {t(activeCompany.status === 'suspended' ? 'error_companySuspended' : 'error_companyNotVerified')}
        </p>
      )}

      <fieldset className="space-y-2">
        <legend className="text-sm font-semibold">{t('sourceLegend')}</legend>
        <div className="flex flex-wrap gap-2">
          {sourceOptions.map((o) => (
            <label key={o.value} className={`inline-flex min-h-11 cursor-pointer items-center gap-2 rounded-md border px-4 text-sm font-medium focus-within:ring-2 focus-within:ring-accent ${source === o.value ? 'border-accent bg-accent/10' : 'border-surface-border-strong bg-bg-muted'}`}>
              <input type="radio" name="rfq-source" value={o.value} checked={source === o.value} onChange={() => { setSource(o.value); setFailure(null); }} />
              {o.label}
            </label>
          ))}
        </div>
      </fieldset>

      {source === 'cart' && (
        <p data-testid="rfq-source-cart" className="rounded-md border border-surface-border bg-bg-muted p-3 text-sm text-text-muted">
          {t('cartInfo')}{' '}
          <Link prefetch={false} href={toPath('/cart')} className="font-semibold text-accent hover:underline">{t('viewCart')}</Link>
        </p>
      )}

      {source === 'product' && (
        <div className="space-y-2" data-testid="rfq-source-product">
          <p className="text-sm">{t('productInfo')} <strong>{query.label || query.variantId}</strong></p>
          <label htmlFor="rfq-qty" className="block text-sm font-semibold">{t('quantity')}</label>
          <input id="rfq-qty" type="number" inputMode="numeric" min={1} max={RFQ_MAX_QUANTITY} value={qty} onChange={(e) => setQty(e.target.value)} className="h-11 w-32 rounded-md border border-surface-border-strong bg-bg-muted px-3 text-sm" />
          <p className="text-xs text-text-muted">{t('quantityHint', { max: RFQ_MAX_QUANTITY.toLocaleString('en-US') })}</p>
        </div>
      )}

      {source === 'text' && (
        <div className="space-y-2" data-testid="rfq-source-text">
          <label htmlFor="rfq-text" className="block text-sm font-semibold">{t('textLabel')}</label>
          <textarea
            id="rfq-text"
            ref={areaRef}
            value={text}
            rows={8}
            spellCheck={false}
            autoComplete="off"
            maxLength={RFQ_MAX_TEXT}
            aria-describedby="rfq-text-hint"
            placeholder={tq('placeholder')}
            onChange={(e) => { setText(e.target.value); setFromFile(false); }}
            className="w-full rounded-md border border-surface-border-strong bg-bg-muted p-3 font-mono text-sm placeholder:text-text-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
          />
          <p id="rfq-text-hint" className="text-xs text-text-muted">{t('textHint')}</p>
          <label className="inline-flex min-h-11 cursor-pointer items-center rounded-md border border-surface-border-strong bg-bg-muted px-4 text-sm font-semibold focus-within:ring-2 focus-within:ring-accent hover:bg-surface">
            {tq('fileButton')}
            <input type="file" accept=".csv,.txt,text/csv,text/plain" className="sr-only" onChange={(e) => { void onFile(e.target.files?.[0]); e.target.value = ''; }} />
          </label>
          <p className="text-xs text-text-muted">{tq('fileHint')}</p>
        </div>
      )}

      <div>
        <label htmlFor="rfq-message" className="mb-1 block text-sm font-semibold">{t('message')}</label>
        <textarea id="rfq-message" value={message} onChange={(e) => setMessage(e.target.value)} rows={3} maxLength={RFQ_MAX_MESSAGE} aria-describedby="rfq-message-hint" className="w-full rounded-md border border-surface-border-strong bg-bg-muted p-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent" />
        <p id="rfq-message-hint" className="mt-1 text-xs text-text-muted">{t('messageHint')}</p>
      </div>

      <div className="grid gap-4 sm:grid-cols-2">
        <div>
          <label htmlFor="rfq-needed" className="mb-1 block text-sm font-semibold">{t('neededBy')}</label>
          <input id="rfq-needed" type="date" min={dhakaDay()} max={dhakaDay(365)} value={neededBy} onChange={(e) => setNeededBy(e.target.value)} className="h-11 w-full rounded-md border border-surface-border-strong bg-bg-muted px-3 text-sm" />
        </div>
        {sites.length > 0 && (
          <div>
            <label htmlFor="rfq-site" className="mb-1 block text-sm font-semibold">{t('site')}</label>
            <select id="rfq-site" value={siteId} onChange={(e) => setSiteId(e.target.value)} className="h-11 w-full rounded-md border border-surface-border-strong bg-bg-muted px-3 text-sm">
              <option value="">{t('siteNone')}</option>
              {sites.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
            </select>
          </div>
        )}
      </div>

      <p className="text-xs text-text-muted">{t('noPriceNote')}</p>
      <Button type="submit" variant="accent" size="lg" disabled={busy} data-testid="rfq-submit">
        {busy ? t('sending') : t('submit')}
      </Button>
      <p role="status" className="sr-only">{busy ? t('sending') : ''}</p>
    </form>
  );
}

export function RequestQuoteView() {
  const t = useTranslations('Quotes');
  const toPath = useLocalePath();
  return (
    <PageShell title={t('requestTitle')} subtitle={t('requestIntro')} back={{ href: toPath('/account/company'), label: t('backToCompany') }}>
      <RequireSignIn>
        <React.Suspense fallback={<ListSkeleton rows={3} />}>
          <Form />
        </React.Suspense>
      </RequireSignIn>
    </PageShell>
  );
}

