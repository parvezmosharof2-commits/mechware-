'use client';

import * as React from 'react';

/**
 * Wraps a dialog so its code (Radix Dialog, focus trap, scroll lock) is fetched the first time it is opened, not with the
 * page: it keeps the first-load JS of the console routes under the 170 kB budget. Once opened it stays mounted, so the
 * close animation and the focus return behave as before. The dialog's own props and test ids are unchanged.
 * (`next/dynamic` counted its chunks in the first load; `React.lazy` does not.)
 */
export function lazyDialog<P extends { open: boolean }>(load: () => Promise<{ default: React.ComponentType<P> }>): React.ComponentType<P> {
  const Lazy = React.lazy(load);
  function LazyDialog(props: P) {
    const [seen, setSeen] = React.useState(props.open);
    if (props.open && !seen) setSeen(true);
    if (!seen) return null;
    return (
      <React.Suspense fallback={null}>
        <Lazy {...(props as React.JSX.IntrinsicAttributes & P)} />
      </React.Suspense>
    );
  }
  return LazyDialog;
}
