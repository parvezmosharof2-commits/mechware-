'use client';

import * as React from 'react';
import { defaultTokenVault, type AuthenticatedUser } from '@mechware/api-client';
import { api } from './api';

export type AuthStatus = 'loading' | 'authenticated' | 'anonymous';

interface AuthContextValue {
  status: AuthStatus;
  user: AuthenticatedUser | null;
  /** Call after a successful login / OTP verification (token is already in memory). */
  establishSession: (user: AuthenticatedUser) => void;
  signOut: () => Promise<void>;
}

/**
 * Marker cookie read by the Next middleware so it can redirect signed-out visitors before rendering.
 * It carries no credential. The real session is the httpOnly refresh cookie plus the in-memory access
 * token; every API call is authorised by the API itself (middleware is UX only, CVE-2025-29927).
 */
export const SESSION_MARKER_COOKIE = 'mw_session';

function setMarker(on: boolean) {
  const secure = typeof location !== 'undefined' && location.protocol === 'https:' ? '; Secure' : '';
  document.cookie = on
    ? `${SESSION_MARKER_COOKIE}=1; Path=/; SameSite=Lax; Max-Age=2592000${secure}`
    : `${SESSION_MARKER_COOKIE}=; Path=/; SameSite=Lax; Max-Age=0${secure}`;
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
        if (!defaultTokenVault.getToken() && !(await api.restoreSession())) throw new Error('no session');
        const me = await api.getMe();
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
