'use client';

import * as React from 'react';
import Link from 'next/link';
import { Button } from '@mechware/ui/src/components/Button';
import { Skeleton } from '@mechware/ui/src/components/Skeleton';

/** A failed load or action in words, with Try again when a retry makes sense. */
export function FailureAlert({ failure, onRetry }: { failure: { text: string; retry: boolean } | null; onRetry?: () => void }) {
  if (!failure) return null;
  return (
    <div role="alert" className="flex flex-wrap items-center gap-3 rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">
      <span>{failure.text}</span>
      {failure.retry && onRetry && <Button type="button" variant="outline" size="sm" onClick={onRetry}>Try again</Button>}
    </div>
  );
}

export function NoAccess({ permission }: { permission: string }) {
  return <p role="alert" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">Your role does not allow this action ({permission}).</p>;
}

export function Loading() {
  return <div role="status"><span className="sr-only">Loading</span><Skeleton className="h-40 w-full" /></div>;
}

export function BackLink({ href, children }: { href: string; children: React.ReactNode }) {
  return <Link href={href} className="text-sm font-semibold text-brand-accent underline">{children}</Link>;
}

/** An alert that takes focus when it appears (a failed submit), so a keyboard or screen-reader user lands on it. */
export const FocusAlert = React.forwardRef<HTMLDivElement, { testId: string; children: React.ReactNode }>(function FocusAlert({ testId, children }, ref) {
  return <div ref={ref} tabIndex={-1} role="alert" data-testid={testId} className="rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">{children}</div>;
});

export function Notice({ testId, children }: { testId: string; children: React.ReactNode }) {
  return <div role="status" data-testid={testId} className="rounded-md border border-surface-border bg-bg-subtle p-3 text-sm font-medium">{children}</div>;
}

/** Focuses `ref` whenever `text` becomes set. */
export function useFocusOn(text: string | null) {
  const ref = React.useRef<HTMLDivElement>(null);
  React.useEffect(() => { if (text) ref.current?.focus(); }, [text]);
  return ref;
}
