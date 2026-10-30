'use client';

import * as React from 'react';
import Link from 'next/link';
import { useLocale, useTranslations } from 'next-intl';
import { useRouter } from 'next/navigation';
import { Button, ErrorState, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, Skeleton } from '@mechware/ui';
import { formatCurrency } from '@mechware/ui';
import type { MoneyDto } from '@mechware/api-client';
import { problemCode, problemMessage, problemStatus } from '../../lib/api';
import { useAuth } from '../../lib/auth';

export function useLocalePath() {
  const locale = useLocale();
  return (path: string) => (locale === 'bn' ? `/bn${path}` : path);
}

/** Localised, safe message for an API error. 404/403 are fixed texts; 4xx details from the API are shown as given. */
export function useErrorText() {
  const t = useTranslations('Company.errors');
  return React.useCallback(
    (error: unknown): string => {
      const status = problemStatus(error);
      if (status === 404) return t('notFound');
      if (status === 403) return t('forbidden');
      if (status === 409 || status === 400) return problemMessage(error, t('generic'));
      return t('generic');
    },
    [t]
  );
}

export function useMoney() {
  const locale = useLocale() === 'bn' ? 'bn' : 'en';
  return (m: MoneyDto) => formatCurrency(m.minorUnits / 100, m.currency === 'USD' ? 'USD' : 'BDT', locale);
}

export function isLastAdminError(error: unknown) {
  return problemCode(error) === 'LAST_ADMIN';
}

/** Sends signed-out visitors to the sign-in page; renders children only for a signed-in user. */
export function RequireSignIn({ children }: { children: React.ReactNode }) {
  const { status } = useAuth();
  const router = useRouter();
  const toPath = useLocalePath();
  React.useEffect(() => {
    if (status === 'anonymous') {
      const returnTo = `${window.location.pathname}${window.location.search}`;
      router.replace(`${toPath('/auth/login')}?returnTo=${encodeURIComponent(returnTo)}`);
    }
  }, [status, router, toPath]);
  if (status !== 'authenticated') return <ListSkeleton />;
  return <>{children}</>;
}

export function ListSkeleton({ rows = 3 }: { rows?: number }) {
  const t = useTranslations('Company.common');
  return (
    <div role="status" aria-live="polite" className="space-y-3">
      <span className="sr-only">{t('loading')}</span>
      {Array.from({ length: rows }, (_, i) => (
        <Skeleton key={i} className="h-16 w-full" />
      ))}
    </div>
  );
}

export function LoadError({ error, onRetry }: { error: unknown; onRetry: () => void }) {
  const errorText = useErrorText();
  const t = useTranslations('Company.errors');
  return (
    <ErrorState
      code={problemStatus(error) ?? 'ERR'}
      title={t('load')}
      description={errorText(error)}
      onRetry={onRetry}
      illustration={<span aria-hidden />}
    />
  );
}

export function PageShell({ title, subtitle, children, back }: { title: string; subtitle?: string; children: React.ReactNode; back?: { href: string; label: string } }) {
  return (
    <main className="min-h-screen bg-bg-canvas p-4 text-fg-primary sm:p-6">
      <div className="mx-auto w-full max-w-4xl">
        {back && (
          <Link href={back.href} className="text-xs font-semibold text-brand-primary hover:underline">
            ← {back.label}
          </Link>
        )}
        <h1 className="mt-2 font-display text-2xl font-bold tracking-tight">{title}</h1>
        {subtitle && <p className="mt-1 text-sm text-fg-secondary">{subtitle}</p>}
        <div className="mt-6">{children}</div>
      </div>
    </main>
  );
}

export function ConfirmRemoveDialog({
  open,
  busy,
  onConfirm,
  onClose,
  extra
}: {
  open: boolean;
  busy: boolean;
  onConfirm: () => void;
  onClose: () => void;
  extra?: string | null;
}) {
  const t = useTranslations('Company.common');
  return (
    <Dialog open={open} onOpenChange={(next) => !next && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{t('confirmRemoveTitle')}</DialogTitle>
          <DialogDescription>{extra ?? t('confirmRemoveBody')}</DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="outline" onClick={onClose} disabled={busy}>
            {t('cancel')}
          </Button>
          <Button variant="destructive" onClick={onConfirm} isLoading={busy}>
            {t('remove')}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
