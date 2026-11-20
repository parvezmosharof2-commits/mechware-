'use client';

import { Badge } from '@mechware/ui/src/components/Badge';
import { quoteStatusLabel } from '@mechware/api-client';

/** The status in words (kept apart from the dialog code, so a page that only shows the badge does not load the dialog library). */
export function QuoteStatusBadge({ status }: { status: string }) {
  return <Badge variant={status === 'accepted' ? 'success' : 'outline'} data-testid="quote-status">{quoteStatusLabel(status)}</Badge>;
}
