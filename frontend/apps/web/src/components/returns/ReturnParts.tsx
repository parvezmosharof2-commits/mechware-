'use client';

import * as React from 'react';
import { useTranslations } from 'next-intl';
import { returnStatusKey, returnStatusTone } from '@mechware/api-client';
import { statusChipClass } from '../../lib/orders-ui';

/** The return's status as words (never colour alone). Unknown statuses get a generic label. */
export function ReturnStatusChip({ status }: { status: string }) {
  const t = useTranslations('Returns.status');
  return (
    <span data-testid="return-status" className={`inline-flex items-center rounded-full border px-2.5 py-0.5 text-xs font-semibold ${statusChipClass(returnStatusTone(status))}`}>
      {t(returnStatusKey(status))}
    </span>
  );
}

/** One refusal or failure in words, announced to screen readers and moved to when it appears. */
export function ReturnAlert({ children, testId = 'return-error' }: { children: React.ReactNode; testId?: string }) {
  const ref = React.useRef<HTMLDivElement>(null);
  React.useEffect(() => ref.current?.focus(), []);
  return (
    <div ref={ref} tabIndex={-1} role="alert" data-testid={testId} className="rounded-md border border-fault/50 bg-fault/10 p-3 text-sm text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent">
      {children}
    </div>
  );
}
