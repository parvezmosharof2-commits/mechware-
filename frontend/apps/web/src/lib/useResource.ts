'use client';

import * as React from 'react';

export interface Resource<T> {
  data: T | null;
  error: unknown;
  loading: boolean;
  reload: () => void;
}

/**
 * Loads data on mount and whenever `key` changes. Ignores results of superseded requests, so a slow
 * response for an old key can never overwrite a newer one.
 */
export function useResource<T>(loader: () => Promise<T>, key: string, enabled = true): Resource<T> {
  const [state, setState] = React.useState<{ data: T | null; error: unknown; loading: boolean }>({
    data: null,
    error: null,
    loading: enabled
  });
  const [version, setVersion] = React.useState(0);
  const loaderRef = React.useRef(loader);
  loaderRef.current = loader;

  React.useEffect(() => {
    if (!enabled) return;
    let current = true;
    setState((prev) => ({ ...prev, loading: true, error: null }));
    loaderRef.current().then(
      (data) => current && setState({ data, error: null, loading: false }),
      (error) => current && setState({ data: null, error, loading: false })
    );
    return () => {
      current = false;
    };
  }, [key, version, enabled]);

  const reload = React.useCallback(() => setVersion((v) => v + 1), []);
  return { ...state, reload };
}
