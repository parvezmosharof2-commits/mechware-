'use client';

import * as React from 'react';
import { useRouter } from 'next/navigation';
import { defaultTokenVault, type AuthTokenEnvelope } from '@mechware/api-client';
import { api, isStaff, problemMessage } from '../../lib/api';
import { useAuth } from '../../lib/auth';

type Step = 'credentials' | 'totp';

export default function ConsoleLoginPage() {
  const router = useRouter();
  const auth = useAuth();
  const [email, setEmail] = React.useState('');
  const [password, setPassword] = React.useState('');
  const [step, setStep] = React.useState<Step>('credentials');
  const [code, setCode] = React.useState('');
  const [busy, setBusy] = React.useState(false);
  const [statusMessage, setStatusMessage] = React.useState<string | null>(null);
  const [errorMessage, setErrorMessage] = React.useState<string | null>(null);

  // A visitor who is already signed in does not need this screen.
  React.useEffect(() => {
    if (auth.status === 'authenticated') router.replace('/');
  }, [auth.status, router]);

  /** Lets only staff roles in. A storefront buyer who found this URL is signed straight out again. */
  const finish = async (envelope: AuthTokenEnvelope) => {
    const user = envelope.user;
    if (!user || !isStaff(user.roles)) {
      defaultTokenVault.clear();
      try {
        await api.logout();
      } catch {
        // Nothing to do: the in-memory token is already gone.
      }
      setStep('credentials');
      setPassword('');
      setCode('');
      setErrorMessage('This portal is for MECHWARE staff and sellers. Use the storefront to sign in as a buyer.');
      return;
    }
    auth.establishSession(user);
    router.replace('/');
  };

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setErrorMessage(null);
    setStatusMessage(null);

    const cleaned = code.trim();
    if (step === 'totp') {
      const isSixDigitTotp = /^\d{6}$/.test(cleaned);
      const isRecoveryCode = /^[A-Z0-9]{5}-[A-Z0-9]{5}$/i.test(cleaned);
      if (!isSixDigitTotp && !isRecoveryCode) {
        setErrorMessage('Enter your 6-digit authenticator code or a recovery code like ABCDE-12345.');
        return;
      }
    }

    setBusy(true);
    try {
      const res = await api.login({
        identifier: email.trim(),
        password,
        totpCode: step === 'totp' ? cleaned : undefined
      });
      if (res.data.requiresTwoFactor) {
        setStep('totp');
        setCode('');
        setStatusMessage('Two-factor authentication is required for this account.');
        return;
      }
      setPassword('');
      await finish(res.data);
    } catch (err) {
      setErrorMessage(problemMessage(err, 'Sign-in failed. Please try again.'));
      if (step === 'totp') setCode('');
    } finally {
      setBusy(false);
    }
  };

  return (
    <main className="flex min-h-screen items-center justify-center bg-bg-subtle p-6 text-text">
      <div className="w-full max-w-md space-y-5 rounded-xl border border-surface-border bg-surface p-6">
        <div>
          <span className="font-mono text-xs font-bold uppercase text-accent">MECHWARE OPERATIONS CONSOLE</span>
          <h1 className="mt-1 font-display text-2xl font-bold">Staff &amp; Seller Sign In</h1>
          <p className="mt-1 text-xs text-text-muted">
            Admin and Finance accounts must verify a time-based authenticator code (or a single-use recovery code).
          </p>
        </div>

        {errorMessage && (
          <div role="alert" className="rounded-md border border-fault bg-bg-subtle p-3 text-xs font-medium text-fault">
            {errorMessage}
          </div>
        )}
        {statusMessage && (
          <div role="status" className="rounded-md border border-surface-border bg-bg-subtle p-3 text-xs text-accent">
            {statusMessage}
          </div>
        )}

        <form onSubmit={submit} className="space-y-4" noValidate>
          {step === 'credentials' ? (
            <>
              <div>
                <label htmlFor="console-email" className="block text-xs font-semibold text-text-muted">
                  Staff or seller email
                </label>
                <input
                  id="console-email"
                  type="email"
                  required
                  autoComplete="username"
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                  className="mt-1 w-full rounded-md border border-surface-border bg-bg-subtle px-3 py-2 text-sm text-text"
                />
              </div>
              <div>
                <label htmlFor="console-password" className="block text-xs font-semibold text-text-muted">
                  Password
                </label>
                <input
                  id="console-password"
                  type="password"
                  required
                  autoComplete="current-password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  className="mt-1 w-full rounded-md border border-surface-border bg-bg-subtle px-3 py-2 text-sm text-text"
                />
              </div>
            </>
          ) : (
            <div>
              <label htmlFor="console-code" className="block text-xs font-semibold text-accent">
                6-digit authenticator code or single-use recovery code
              </label>
              <input
                id="console-code"
                type="text"
                required
                autoComplete="one-time-code"
                inputMode="text"
                value={code}
                onChange={(e) => setCode(e.target.value)}
                placeholder="123456 or ABCDE-12345"
                className="mt-1 w-full rounded-md border border-surface-border bg-bg-subtle px-3 py-2 font-mono text-base tracking-widest text-text"
              />
            </div>
          )}
          <button
            type="submit"
            disabled={busy || !email.trim() || (step === 'credentials' ? password.length === 0 : code.trim().length === 0)}
            className="w-full rounded-md bg-accent px-4 py-2.5 text-sm font-semibold text-accent-fg disabled:cursor-not-allowed disabled:opacity-60"
          >
            {step === 'credentials' ? 'Sign in' : 'Verify and unlock console'}
          </button>
          {step === 'totp' && (
            <button
              type="button"
              onClick={() => {
                setStep('credentials');
                setCode('');
                setErrorMessage(null);
                setStatusMessage(null);
              }}
              className="w-full text-xs font-semibold text-text-muted hover:text-text"
            >
              Start over
            </button>
          )}
        </form>
      </div>
    </main>
  );
}
