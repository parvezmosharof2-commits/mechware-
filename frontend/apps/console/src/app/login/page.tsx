'use client';

import * as React from 'react';
import { useRouter } from 'next/navigation';
import { defaultTokenVault, type AuthTokenEnvelope, type TwoFactorSecret } from '@mechware/api-client';
import { api, isStaff, problemMessage, problemStatus } from '../../lib/api';
import { useAuth } from '../../lib/auth';

type Step = 'credentials' | 'totp' | 'setupCode' | 'enroll';

export default function ConsoleLoginPage() {
  const router = useRouter();
  const auth = useAuth();
  const [email, setEmail] = React.useState('');
  const [password, setPassword] = React.useState('');
  const [step, setStep] = React.useState<Step>('credentials');
  const [code, setCode] = React.useState('');
  const [enrollmentToken, setEnrollmentToken] = React.useState<string | null>(null);
  const [enrollment, setEnrollment] = React.useState<TwoFactorSecret | null>(null);
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
      resetToCredentials();
      setErrorMessage('This portal is for MECHWARE staff and sellers. Use the storefront to sign in as a buyer.');
      return;
    }
    auth.establishSession(user);
    router.replace('/');
  };

  const resetToCredentials = () => {
    setStep('credentials');
    setPassword('');
    setCode('');
    setEnrollmentToken(null);
    setEnrollment(null);
  };

  /** Password + administrator's setup code accepted: show the new secret once, then confirm it with a first code. */
  const beginEnrollment = (token: string, secret: TwoFactorSecret) => {
    setEnrollmentToken(token);
    setEnrollment(secret);
    setPassword('');
    setCode('');
    setStep('enroll');
    setStatusMessage('Add this account to your authenticator app and save the recovery codes, then enter the first code.');
  };

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setErrorMessage(null);
    setStatusMessage(null);

    const cleaned = code.trim();
    if (step === 'enroll') {
      if (!/^\d{6}$/.test(cleaned) || !enrollmentToken) {
        setErrorMessage('Enter the 6-digit code your authenticator app shows for this account.');
        return;
      }
      setBusy(true);
      try {
        const res = await api.completeTwoFactorEnrollment(enrollmentToken, cleaned);
        setEnrollment(null);
        setEnrollmentToken(null);
        await finish(res.data);
      } catch (err) {
        const message = problemMessage(err, 'That code did not work. Try the next code, or start over.');
        if (problemStatus(err) === 401) {
          // The 10-minute token is spent or expired: only a fresh password + setup code can continue.
          resetToCredentials();
        } else {
          setCode('');
        }
        setErrorMessage(message);
      } finally {
        setBusy(false);
      }
      return;
    }
    if (step === 'setupCode' && !/^[A-Z2-9]{5}-?[A-Z2-9]{5}$/i.test(cleaned)) {
      setErrorMessage('Enter the setup code from your administrator, like ABCDE-23456.');
      return;
    }
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
        totpCode: step === 'totp' ? cleaned : undefined,
        setupCode: step === 'setupCode' ? cleaned : undefined
      });
      if (res.data.requiresTwoFactorEnrollment) {
        if (res.data.enrollmentToken && res.data.twoFactorSetup) {
          beginEnrollment(res.data.enrollmentToken, res.data.twoFactorSetup);
        } else {
          setStep('setupCode');
          setCode('');
          setStatusMessage(
            'Your role requires an authenticator. Enter the one-time setup code an administrator gave you to set one up.'
          );
        }
        return;
      }
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
      if (step === 'totp' || step === 'setupCode') setCode('');
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
            Staff accounts must verify a time-based authenticator code (or a single-use recovery code).
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
              {step === 'enroll' && enrollment && (
                <div className="mb-4 space-y-3 rounded-md border border-surface-border bg-bg-subtle p-3 text-xs">
                  <div>
                    <p className="font-semibold text-text">1. Add to your authenticator app</p>
                    <p className="mt-1 text-text-muted">Enter this setup key manually, or open the link on the phone that has the app.</p>
                    <code className="mt-1 block break-all font-mono text-sm tracking-wider text-text" aria-label="Authenticator setup key">
                      {enrollment.secret.replace(/(.{4})/g, '$1 ').trim()}
                    </code>
                    <a href={enrollment.otpAuthUri} className="mt-1 inline-block font-semibold text-accent underline">
                      Open in authenticator app
                    </a>
                  </div>
                  <div>
                    <p className="font-semibold text-text">2. Save these recovery codes somewhere safe</p>
                    <p className="mt-1 text-text-muted">Each works once if you lose the phone. They are not shown again.</p>
                    <ul className="mt-1 grid grid-cols-2 gap-1 font-mono text-text">
                      {enrollment.recoveryCodes.map((rc) => (
                        <li key={rc}>{rc}</li>
                      ))}
                    </ul>
                  </div>
                  <p className="font-semibold text-text">3. Enter the code the app shows now</p>
                </div>
              )}
              <label htmlFor="console-code" className="block text-xs font-semibold text-accent">
                {step === 'enroll'
                  ? '6-digit authenticator code'
                  : step === 'setupCode'
                    ? 'One-time setup code from your administrator'
                    : '6-digit authenticator code or single-use recovery code'}
              </label>
              <input
                id="console-code"
                type="text"
                required
                autoComplete="one-time-code"
                inputMode="text"
                value={code}
                onChange={(e) => setCode(e.target.value)}
                placeholder={step === 'enroll' ? '123456' : step === 'setupCode' ? 'ABCDE-23456' : '123456 or ABCDE-12345'}
                className="mt-1 w-full rounded-md border border-surface-border bg-bg-subtle px-3 py-2 font-mono text-base tracking-widest text-text"
              />
            </div>
          )}
          <button
            type="submit"
            disabled={busy || !email.trim() || (step === 'credentials' ? password.length === 0 : code.trim().length === 0)}
            className="w-full rounded-md bg-accent px-4 py-2.5 text-sm font-semibold text-accent-fg disabled:cursor-not-allowed disabled:opacity-60"
          >
            {step === 'credentials'
              ? 'Sign in'
              : step === 'enroll'
                ? 'Turn on 2FA and unlock console'
                : step === 'setupCode'
                  ? 'Continue'
                  : 'Verify and unlock console'}
          </button>
          {step !== 'credentials' && (
            <button
              type="button"
              onClick={() => {
                resetToCredentials();
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
