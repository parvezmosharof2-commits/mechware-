'use client';

import * as React from 'react';
import { Badge } from '@mechware/ui/src/components/Badge';
import { Button } from '@mechware/ui/src/components/Button';
import { deskPageCount, rfqSla, rfqStatusLabel, type StaffRfqSummary } from '@mechware/api-client';
import { QuoteStatusBadge } from './QuoteStatusBadge';

/**
 * The first-quote clock of an RFQ. The verdict is always in words ("SLA breached", "Within SLA"), never only a colour, and the sentence
 * under the badge says how long it waited and when the answer was due (Dhaka time).
 */
export function SlaBadge({ rfq }: { rfq: Pick<StaffRfqSummary, 'status' | 'firstQuoteSentAt' | 'slaDueAt' | 'slaWorkingMinutes' | 'slaBreached'> }) {
  const sla = rfqSla(rfq);
  return (
    <span className="inline-flex flex-col items-start gap-1" data-testid="sla" data-sla-state={sla.state}>
      <Badge variant="outline" className={sla.breached ? 'border-fault font-bold text-fault' : undefined} data-testid="sla-badge">{sla.label}</Badge>
      <span className="text-xs text-text-muted" data-testid="sla-detail">{sla.detail}</span>
    </span>
  );
}

export function RfqStatusBadge({ status }: { status: string }) {
  return <Badge variant="outline" data-testid="rfq-status">{rfqStatusLabel(status)}</Badge>;
}

export { QuoteStatusBadge };

export function Pager({ page, total, pageSize, loading, noun, onPage, onRefresh }: {
  page: number; total: number; pageSize: number; loading: boolean; noun: string; onPage: (page: number) => void; onRefresh: () => void;
}) {
  const pages = deskPageCount(total, pageSize);
  return (
    <div className="flex flex-wrap items-center gap-3 text-xs">
      <Button type="button" variant="outline" size="sm" disabled={page <= 1 || loading} onClick={() => onPage(page - 1)}>Previous</Button>
      <span aria-live="polite" data-testid="pager">Page {page} of {pages} ({total} {noun})</span>
      <Button type="button" variant="outline" size="sm" disabled={page >= pages || loading} onClick={() => onPage(page + 1)}>Next</Button>
      <Button type="button" variant="ghost" size="sm" onClick={onRefresh}>Refresh</Button>
    </div>
  );
}

export { ReasonDialog } from './ReasonDialog';
