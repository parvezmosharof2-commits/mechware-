'use client';

import * as React from 'react';
import { defaultTokenVault, type AuthenticatedUser } from '@mechware/api-client';
import { api, isStaff } from './api';

export type AuthStatus = 'loading' | 'authenticated' | 'anonymous';

interface AuthContextValue {
  status: AuthStatus;
  user: AuthenticatedUser | null;
  establishSession: (user: AuthenticatedUser) => void;
  signOut: () => Promise<void>;
}

/**
 * Credential-free marker read by the Next middleware. The refresh cookie is scoped to /api/v1/auth and never
 * reaches console routes, so the middleware cannot see it. This is a UX redirect only: every API call is
 * authorised by the API (middleware is never an authorisation boundary, CVE-2025-29927).
 */
export const CONSOLE_SESSION_MARKER = 'mw_console_session';

function setMarker(on: boolean) {
  const secure = location.protocol === 'https:' ? '; Secure' : '';
  document.cookie = on
    ? `${CONSOLE_SESSION_MARKER}=1; Path=/; SameSite=Lax; Max-Age=2592000${secure}`
    : `${CONSOLE_SESSION_MARKER}=; Path=/; SameSite=Lax; Max-Age=0${secure}`;
}

function hasMarker(): boolean {
  return document.cookie.split('; ').some((c) => c.startsWith(`${CONSOLE_SESSION_MARKER}=`));
}

const AuthContext = React.createContext<AuthContextValue>({
  status: 'anonymous',
  user: null,
  establishSession: () => undefined,
  signOut: async () => undefined
});

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [status, setStatus] = React.useState<AuthStatus>('loading');
  const [user, setUser] = React.useState<AuthenticatedUser | null>(null);

  React.useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        if (!defaultTokenVault.getToken() && !hasMarker()) throw new Error('no session');
        if (!defaultTokenVault.getToken() && !(await api.restoreSession())) throw new Error('no session');
        const me = await api.getMe();
        if (!isStaff(me.data.roles)) throw new Error('not staff');
        if (cancelled) return;
        setUser(me.data);
        setStatus('authenticated');
        setMarker(true);
      } catch {
        if (cancelled) return;
        defaultTokenVault.clear();
        setUser(null);
        setStatus('anonymous');
        setMarker(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  const establishSession = React.useCallback((next: AuthenticatedUser) => {
    setUser(next);
    setStatus('authenticated');
    setMarker(true);
  }, []);

  const signOut = React.useCallback(async () => {
    try {
      await api.logout();
    } catch {
      // The local session is dropped even if the server could not be reached.
    }
    defaultTokenVault.clear();
    setUser(null);
    setStatus('anonymous');
    setMarker(false);
  }, []);

  const value = React.useMemo(() => ({ status, user, establishSession, signOut }), [status, user, establishSession, signOut]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  return React.useContext(AuthContext);
}
