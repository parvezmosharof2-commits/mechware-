'use client';

import * as React from 'react';
import dynamic from 'next/dynamic';

function currentLocale(): 'en' | 'bn' {
  return typeof window !== 'undefined' && /^\/bn(\/|$)/.test(window.location.pathname) ? 'bn' : 'en';
}

function signInUrl(locale: 'en' | 'bn') {
  const returnTo = `${window.location.pathname}${window.location.search}`;
  return `${locale === 'bn' ? '/bn/auth/login' : '/auth/login'}?returnTo=${encodeURIComponent(returnTo)}`;
}

/** The non-secret marker cookie set on sign-in (lib/auth.tsx, SESSION_MARKER_COOKIE); read here without importing the auth bundle. */
function probablySignedIn(): boolean {
  return typeof document !== 'undefined' && document.cookie.split('; ').some((c) => c.startsWith('mw_session='));
}

/** `hidden`: keeps the slot's size (no layout shift) without flashing "Sign In" to a visitor who is signed in. */
function SignInButton({ locale, hidden }: { locale: 'en' | 'bn'; hidden?: boolean }) {
  return (
    <button
      type="button"
      aria-hidden={hidden || undefined}
      tabIndex={hidden ? -1 : undefined}
      onClick={() => window.location.assign(signInUrl(locale))}
      className={`rounded-md bg-brand-primary px-4 py-2 text-xs font-semibold text-fg-on-accent transition-colors hover:bg-brand-primary-hover${hidden ? ' invisible' : ''}`}
    >
      {locale === 'bn' ? 'সাইন ইন / নিবন্ধন' : 'Sign In / Register'}
    </button>
  );
}

// The session menu (token vault, auth context) loads after the page is idle, so it does not count against the first
// load. Until then the signed-out button is a plain control that sends the visitor to the login page. The placeholder
// also stays on screen while the chunk loads: an empty slot would change the header height (layout shift).
const HeaderAccountLive = dynamic<{ locale: 'en' | 'bn' }>(() => import('./HeaderAccountLive').then((m) => m.HeaderAccountLive), {
  ssr: false,
  loading: () => <SignInButton locale={currentLocale()} hidden={probablySignedIn()} />
});

/** The only client code in the header: session state lives in the browser. */
export function HeaderAccount({ locale, initialSignedIn = false }: { locale: 'en' | 'bn'; initialSignedIn?: boolean }) {
  const [live, setLive] = React.useState(false);
  const [signedIn, setSignedIn] = React.useState(initialSignedIn);
  React.useEffect(() => {
    // A signed-in visitor needs the real menu now; everyone else gets it when the page is idle.
    if (probablySignedIn()) {
      setSignedIn(true);
      setLive(true);
      return;
    }
    const w = window as Window & { requestIdleCallback?: (cb: () => void, o?: { timeout: number }) => number; cancelIdleCallback?: (h: number) => void };
    if (w.requestIdleCallback) {
      const h = w.requestIdleCallback(() => setLive(true), { timeout: 3000 });
      return () => w.cancelIdleCallback?.(h);
    }
    const t = window.setTimeout(() => setLive(true), 2000);
    return () => window.clearTimeout(t);
  }, []);

  return live ? <HeaderAccountLive locale={locale} /> : <SignInButton locale={locale} hidden={signedIn} />;
}
