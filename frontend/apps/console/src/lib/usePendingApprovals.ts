'use client';

import * as React from 'react';
import { priceListsApi } from './api';

export const APPROVALS_CHANGED = 'mw:approvals-changed';

/** Number of price lists waiting for approval, for the sidebar badge. Quietly null when not allowed or on any failure. */
export function usePendingApprovals(enabled: boolean): number | null {
  const [count, setCount] = React.useState<number | null>(null);
  React.useEffect(() => {
    if (!enabled) return;
    let current = true;
    const load = () => {
      priceListsApi.list({ status: 'PendingApproval', page: 1, pageSize: 1 }).then(
        (r) => current && setCount(r.data.total),
        () => current && setCount(null)
      );
    };
    load();
    window.addEventListener(APPROVALS_CHANGED, load);
    return () => { current = false; window.removeEventListener(APPROVALS_CHANGED, load); };
  }, [enabled]);
  return enabled ? count : null;
}
