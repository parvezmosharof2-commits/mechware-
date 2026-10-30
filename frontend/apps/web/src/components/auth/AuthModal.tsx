'use client';

import * as React from 'react';
import { useRouter } from 'next/navigation';
import type { AuthTokenEnvelope } from '@mechware/api-client';
import { api, problemFieldErrors, problemMessage } from '../../lib/api';
import { useAuth } from '../../lib/auth';

export type AuthViewMode = 'signin' | 'signup' | 'otp' | 'forgot' | 'reset';

export interface AuthTranslations {
  signInTitle: string;
  signInSubtitle: string;
  signUpTitle: string;
  signUpSubtitle: string;
  otpTitle: string;
  otpSubtitle: string;
  forgotTitle: string;
  forgotSubtitle: string;
  emailOrPhoneLabel: string;
  emailLabel: string;
  phoneLabel: string;
  fullNameLabel: string;
  passwordLabel: string;
  totpCodeLabel: string;
  otpCodeLabel: string;
  signInButton: string;
  signUpButton: string;
  verifyOtpButton: string;
  googleSignInButton: string;
  forgotPasswordLink: string;
  switchToSignUp: string;
  switchToSignIn: string;
  inMemoryTokenNotice: string;
}

export const AUTH_COPY_BY_LOCALE: Record<'en' | 'bn', AuthTranslations> = {
  en: {
    signInTitle: 'Procurement & Engineer Sign In',
    signInSubtitle: 'Access verified BDT industrial pricing, RFQ quotes, and factory order tracking.',
    signUpTitle: 'Register Industrial Buyer Account',
    signUpSubtitle: 'Create your enterprise MECHWARE account with Bangladesh mobile OTP verification.',
    otpTitle: 'Verify 6-Digit Security Code',
    otpSubtitle: 'Enter the passcode sent to your registered Bangladesh phone or email.',
    forgotTitle: 'Reset Account Password',
    forgotSubtitle: 'We will send a bound 6-digit recovery code to your verified email or mobile.',
    emailOrPhoneLabel: 'Corporate Email or BD Phone (+880)',
    emailLabel: 'Corporate Email Address',
    phoneLabel: 'Bangladesh Mobile Number (+8801XXXXXXXXX)',
    fullNameLabel: 'Full Name / Engineer Designation',
    passwordLabel: 'Security Password',
    totpCodeLabel: '6-Digit Authenticator (TOTP) or Recovery Code',
    otpCodeLabel: '6-Digit OTP Verification Code',
    signInButton: 'Sign In to MECHWARE',
    signUpButton: 'Create Account & Send OTP',
    verifyOtpButton: 'Verify & Activate Session',
    googleSignInButton: 'Continue with Google Workspace OIDC',
    forgotPasswordLink: 'Forgot password?',
    switchToSignUp: 'New to MECHWARE? Register factory buyer account',
    switchToSignIn: 'Already registered? Sign in',
    inMemoryTokenNotice:
      'Secure sign-in: your session stays protected and ends when you sign out or close your browser tab.'
  },
  bn: {
    signInTitle: 'প্রকিউরমেন্ট ও ইঞ্জিনিয়ার সাইন ইন',
    signInSubtitle: 'ভেরিফায়েড ইন্ডাস্ট্রিয়াল মূল্য, আরএফকিউ কোটেশন এবং ফ্যাক্টরি অর্ডার ট্র্যাকিং অ্যাক্সেস করুন।',
    signUpTitle: 'ইন্ডাস্ট্রিয়াল বায়ার অ্যাকাউন্ট নিবন্ধন',
    signUpSubtitle: 'বাংলাদেশ মোবাইল ওটিপি ভেরিফিকেশনসহ আপনার মেকওয়্যার এন্টারপ্রাইজ অ্যাকাউন্ট তৈরি করুন।',
    otpTitle: '৬-ডিজিট নিরাপত্তা কোড যাচাই করুন',
    otpSubtitle: 'আপনার নিবন্ধিত বাংলাদেশ মোবাইল নম্বর বা ইমেইলে পাঠানো কোডটি লিখুন।',
    forgotTitle: 'অ্যাকাউন্ট পাসওয়ার্ড রিসেট করুন',
    forgotSubtitle: 'আপনার যাচাইকৃত ইমেইল বা মোবাইলে একটি ৬-ডিজিট রিকভারি কোড পাঠানো হবে।',
    emailOrPhoneLabel: 'কর্পোরেট ইমেইল বা মোবাইল নম্বর (+880)',
    emailLabel: 'কর্পোরেট ইমেইল ঠিকানা',
    phoneLabel: 'বাংলাদেশ মোবাইল নম্বর (+8801XXXXXXXXX)',
    fullNameLabel: 'পূর্ণ নাম / ইঞ্জিনিয়ার পদবি',
    passwordLabel: 'নিরাপত্তা পাসওয়ার্ড',
    totpCodeLabel: '৬-ডিজিট অথেনটিকেটর (TOTP) বা রিকভারি কোড',
    otpCodeLabel: '৬-ডিজিট ওটিপি যাচাইকরণ কোড',
    signInButton: 'মেকওয়্যারে সাইন ইন করুন',
    signUpButton: 'অ্যাকাউন্ট তৈরি করুন ও ওটিপি পাঠান',
    verifyOtpButton: 'যাচাই করুন ও সেশন চালু করুন',
    googleSignInButton: 'Google Workspace OIDC দিয়ে চালিয়ে যান',
    forgotPasswordLink: 'পাসওয়ার্ড ভুলে গেছেন?',
    switchToSignUp: 'মেকওয়্যারে নতুন? ফ্যাক্টরি বায়ার অ্যাকাউন্ট খুলুন',
    switchToSignIn: 'ইতিমধ্যে অ্যাকাউন্ট আছে? সাইন ইন করুন',
    inMemoryTokenNotice:
      'নিরাপদ সাইন-ইন: আপনার সেশন সুরক্ষিত থাকে এবং সাইন-আউট করলে বা ব্রাউজার ট্যাব বন্ধ করলে শেষ হয়ে যায়।'
  }
};

export interface AuthSurfaceProps {
  locale?: 'en' | 'bn';
  initialMode?: AuthViewMode;
  returnTo?: string;
  isModal?: boolean;
  onClose?: () => void;
  onAuthenticated?: (payload: {
    accessToken: string;
    email: string;
    fullName: string;
    roles: string[];
    returnTo: string;
  }) => void;
}

export function AuthFlowSurface({
  locale = 'en',
  initialMode = 'signin',
  returnTo = '/',
  isModal = false,
  onClose,
  onAuthenticated
}: AuthSurfaceProps) {
  const copy = AUTH_COPY_BY_LOCALE[locale];
  const [mode, setMode] = React.useState<AuthViewMode>(initialMode);
  const [identifier, setIdentifier] = React.useState('');
  const [email, setEmail] = React.useState('');
  const [phone, setPhone] = React.useState('+880');
  const [fullName, setFullName] = React.useState('');
  const [password, setPassword] = React.useState('');
  const [otpCode, setOtpCode] = React.useState('');
  const [totpCode, setTotpCode] = React.useState('');
  const [requiresTwoFactor, setRequiresTwoFactor] = React.useState(false);
  const [referenceId, setReferenceId] = React.useState('');
  const [statusMessage, setStatusMessage] = React.useState<string | null>(null);
  const [errorMessage, setErrorMessage] = React.useState<string | null>(null);

  const router = useRouter();
  const auth = useAuth();
  const [submitting, setSubmitting] = React.useState(false);
  const [otpTarget, setOtpTarget] = React.useState<{ recipient: string; type: 'PhoneVerify' | 'EmailVerify' }>({
    recipient: '',
    type: 'PhoneVerify'
  });

  const t = (en: string, bn: string) => (locale === 'bn' ? bn : en);
  const homePath = locale === 'bn' ? '/bn' : '/';

  /** Shows the API's own message (already safe and localised-neutral) or a generic fallback. */
  const showApiError = (err: unknown) => {
    const fields = Object.values(problemFieldErrors(err));
    const base = problemMessage(
      err,
      t('Something went wrong. Please try again.', 'কিছু ভুল হয়েছে। অনুগ্রহ করে আবার চেষ্টা করুন।')
    );
    setErrorMessage(fields.length > 0 ? `${base} ${fields.join(' ')}` : base);
  };

  /** Finishes a successful authentication: shares the session with the app, then leaves the auth screen. */
  const completeAuthentication = (envelope: AuthTokenEnvelope) => {
    const user = envelope.user;
    if (user) auth.establishSession(user);
    const target = returnTo === '/' ? homePath : returnTo;
    if (onAuthenticated) {
      onAuthenticated({
        accessToken: envelope.accessToken,
        email: user?.email ?? '',
        fullName: user?.fullName ?? '',
        roles: user?.roles ?? [],
        returnTo: target
      });
    } else {
      router.replace(target);
    }
  };

  const handleSignInSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setErrorMessage(null);
    setStatusMessage(null);

    if (!identifier.trim() || password.length < 8) {
      setErrorMessage(
        t(
          'Valid identifier and minimum 8-character password are required.',
          'সঠিক ইমেইল/ফোন এবং ন্যূনতম ৮ অক্ষরের পাসওয়ার্ড প্রয়োজন।'
        )
      );
      return;
    }

    setSubmitting(true);
    try {
      const res = await api.login({
        identifier: identifier.trim(),
        password,
        totpCode: requiresTwoFactor ? totpCode.trim() : undefined
      });
      if (res.data.requiresTwoFactor) {
        setRequiresTwoFactor(true);
        setStatusMessage(
          t(
            'Two-factor authentication is required for this role. Enter your 6-digit TOTP or recovery code.',
            'এই রোলের জন্য ২-ফ্যাক্টর অথেনটিকেশন আবশ্যক। ৬-ডিজিট TOTP বা রিকভারি কোড দিন।'
          )
        );
        return;
      }
      setPassword('');
      completeAuthentication(res.data);
    } catch (err) {
      showApiError(err);
    } finally {
      setSubmitting(false);
    }
  };

  const handleSignUpSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setErrorMessage(null);
    setStatusMessage(null);

    const hasUpper = /[A-Z]/.test(password);
    const hasLower = /[a-z]/.test(password);
    const hasDigit = /\d/.test(password);
    const hasSpecial = /[^A-Za-z0-9]/.test(password);

    if (password.length < 8 || !hasUpper || !hasLower || !hasDigit || !hasSpecial) {
      setErrorMessage(
        t(
          'Password policy: min 8 chars with uppercase, lowercase, digit, and special character.',
          'পাসওয়ার্ডে ন্যূনতম ৮ অক্ষর, বড় হাতের অক্ষর, ছোট হাতের অক্ষর, সংখ্যা এবং বিশেষ চিহ্ন থাকতে হবে।'
        )
      );
      return;
    }

    setSubmitting(true);
    try {
      const res = await api.register({
        email: email.trim(),
        phone: phone.trim() || undefined,
        password,
        fullName: fullName.trim(),
        locale
      });
      const viaPhone = res.data.verificationChannel === 'phone_otp';
      setOtpTarget({ recipient: viaPhone ? phone.trim() : email.trim(), type: viaPhone ? 'PhoneVerify' : 'EmailVerify' });
      setReferenceId(res.data.referenceId);
      setOtpCode('');
      setMode('otp');
      setStatusMessage(
        t(
          `A 6-digit code was sent to ${viaPhone ? phone.trim() : email.trim()}.`,
          `${viaPhone ? phone.trim() : email.trim()}-এ ৬-ডিজিট কোড পাঠানো হয়েছে।`
        )
      );
    } catch (err) {
      showApiError(err);
    } finally {
      setSubmitting(false);
    }
  };

  const handleVerifyOtpSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setErrorMessage(null);
    setStatusMessage(null);

    if (!/^\d{6}$/.test(otpCode.trim())) {
      setErrorMessage(t('Please enter a valid 6-digit numeric OTP code.', '৬-ডিজিট সংখ্যার ওটিপি কোড প্রদান করুন।'));
      return;
    }

    setSubmitting(true);
    try {
      const res = await api.verifyOtp({
        recipient: otpTarget.recipient,
        code: otpCode.trim(),
        type: otpTarget.type,
        referenceId
      });
      setPassword('');
      completeAuthentication(res.data);
    } catch (err) {
      showApiError(err);
    } finally {
      setSubmitting(false);
    }
  };

  const handleRecoverySubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setErrorMessage(null);
    setStatusMessage(null);
    setSubmitting(true);
    try {
      if (mode === 'forgot') {
        const res = await api.forgotPassword(identifier.trim());
        setReferenceId(res.data.referenceId);
        setOtpCode('');
        setPassword('');
        setMode('reset');
        setStatusMessage(
          t('If the account exists, a reset code has been sent.', 'অ্যাকাউন্ট থাকলে একটি রিসেট কোড পাঠানো হয়েছে।')
        );
      } else {
        await api.resetPassword(identifier.trim(), referenceId, otpCode.trim(), password);
        setPassword('');
        setOtpCode('');
        setMode('signin');
        setStatusMessage(
          t(
            'Password updated. All previous sessions were signed out; sign in with the new password.',
            'পাসওয়ার্ড পরিবর্তন হয়েছে। আগের সব সেশন বাতিল; নতুন পাসওয়ার্ডে সাইন ইন করুন।'
          )
        );
      }
    } catch (err) {
      showApiError(err);
    } finally {
      setSubmitting(false);
    }
  };

  /** Google sign-in needs a configured OAuth client. Until then it is disabled instead of faking a session. */
  const googleConfigured = Boolean(process.env.NEXT_PUBLIC_GOOGLE_CLIENT_ID);
  const handleGoogleOAuth = () => {
    setErrorMessage(
      t('Google sign-in is not configured for this environment.', 'এই পরিবেশে Google সাইন-ইন কনফিগার করা নেই।')
    );
  };

  // A full-page auth screen owns the page's level-one heading; inside a dialog it is a sub-heading.
  const TitleTag = isModal ? 'h2' : 'h1';

  const containerContent = (
    <div className="w-full max-w-md rounded-lg border border-border-subtle bg-bg-surface p-6 text-fg-primary shadow-md">
      <div className="flex items-start justify-between gap-4 border-b border-border-subtle pb-4">
        <div>
          <span className="inline-block rounded-sm bg-bg-elevated px-2 py-0.5 font-mono text-xs font-semibold text-brand-accent">
            {locale === 'bn' ? 'মেকওয়্যার ইন্ডাস্ট্রিয়াল পোর্টাল (BN)' : 'MECHWARE INDUSTRIAL PORTAL (EN)'}
          </span>
          <TitleTag className="mt-2 font-display text-xl font-bold tracking-tight text-fg-primary">
            {mode === 'signin' && copy.signInTitle}
            {mode === 'signup' && copy.signUpTitle}
            {mode === 'otp' && copy.otpTitle}
            {(mode === 'forgot' || mode === 'reset') && copy.forgotTitle}
          </TitleTag>
          <p className="mt-1 text-xs text-fg-secondary">
            {mode === 'signin' && copy.signInSubtitle}
            {mode === 'signup' && copy.signUpSubtitle}
            {mode === 'otp' && copy.otpSubtitle}
            {(mode === 'forgot' || mode === 'reset') && copy.forgotSubtitle}
          </p>
        </div>
        {isModal && onClose && (
          <button
            type="button"
            onClick={onClose}
            aria-label="Close authentication modal"
            className="rounded-sm border border-border-subtle bg-bg-elevated px-2.5 py-1 text-xs font-semibold text-fg-secondary hover:text-fg-primary"
          >
            ✕
          </button>
        )}
      </div>

      {errorMessage && (
        <div
          role="alert"
          className="mt-4 rounded-md border border-state-danger bg-bg-elevated p-3 text-xs font-medium text-state-danger"
        >
          {errorMessage}
        </div>
      )}

      {statusMessage && (
        <div
          role="status"
          className="mt-4 rounded-md border border-state-success bg-bg-elevated p-3 text-xs font-medium text-state-success"
        >
          {statusMessage}
        </div>
      )}

      {mode === 'signin' && (
        <form onSubmit={handleSignInSubmit} className="mt-5 space-y-4">
          <div>
            <label htmlFor="auth-1" className="block text-xs font-semibold text-fg-secondary">
              {copy.emailOrPhoneLabel}
            </label>
            <input id="auth-1"
              type="text"
              required
              value={identifier}
              onChange={(e) => setIdentifier(e.target.value)}
              className="mt-1 w-full rounded-md border border-border-subtle bg-bg-canvas px-3 py-2 text-sm text-fg-primary focus:border-brand-primary focus:outline-none"
            />
          </div>
          <div>
            <div className="flex items-center justify-between">
              <label htmlFor="auth-pw" className="block text-xs font-semibold text-fg-secondary">
                {copy.passwordLabel}
              </label>
              <button
                type="button"
                onClick={() => setMode('forgot')}
                className="text-xs font-semibold text-brand-primary hover:underline"
              >
                {copy.forgotPasswordLink}
              </button>
            </div>
            <input
              id="auth-pw"
              type="password"
              required
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              autoComplete="current-password"
              placeholder="••••••••••••"
              className="mt-1 w-full rounded-md border border-border-subtle bg-bg-canvas px-3 py-2 text-sm text-fg-primary focus:border-brand-primary focus:outline-none"
            />
          </div>

          {requiresTwoFactor && (
            <div>
              <label htmlFor="auth-2" className="block text-xs font-semibold text-brand-accent">
                {copy.totpCodeLabel}
              </label>
              <input id="auth-2"
                type="text"
                required
                value={totpCode}
                onChange={(e) => setTotpCode(e.target.value)}
                placeholder="123456 or ABCDE-12345"
                className="mt-1 w-full rounded-md border border-brand-accent bg-bg-canvas px-3 py-2 font-mono text-sm text-fg-primary"
              />
            </div>
          )}

          <button
            type="submit"
            disabled={submitting}
            className="w-full rounded-md bg-brand-primary px-4 py-2.5 text-sm font-semibold text-fg-on-accent transition-colors hover:bg-brand-primary-hover disabled:cursor-not-allowed disabled:opacity-60"
          >
            {copy.signInButton}
          </button>

          <button
            type="button"
            onClick={handleGoogleOAuth}
            disabled={!googleConfigured}
            title={googleConfigured ? undefined : t('Not configured', 'কনফিগার করা নেই')}
            className="w-full rounded-md border border-border-strong bg-bg-elevated px-4 py-2 text-xs font-semibold text-fg-primary transition-colors hover:bg-bg-canvas disabled:cursor-not-allowed disabled:opacity-50"
          >
            {copy.googleSignInButton}
          </button>

          <div className="pt-2 text-center">
            <button
              type="button"
              onClick={() => setMode('signup')}
              className="text-xs font-semibold text-brand-primary hover:underline"
            >
              {copy.switchToSignUp}
            </button>
          </div>
        </form>
      )}

      {mode === 'signup' && (
        <form onSubmit={handleSignUpSubmit} className="mt-5 space-y-4">
          <div>
            <label htmlFor="auth-3" className="block text-xs font-semibold text-fg-secondary">
              {copy.fullNameLabel}
            </label>
            <input id="auth-3"
              type="text"
              required
              value={fullName}
              onChange={(e) => setFullName(e.target.value)}
              placeholder="Engr. Tanvir Ahmed"
              className="mt-1 w-full rounded-md border border-border-subtle bg-bg-canvas px-3 py-2 text-sm text-fg-primary"
            />
          </div>
          <div>
            <label htmlFor="auth-4" className="block text-xs font-semibold text-fg-secondary">
              {copy.emailLabel}
            </label>
            <input id="auth-4"
              type="email"
              required
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              placeholder="procurement@factory.com.bd"
              className="mt-1 w-full rounded-md border border-border-subtle bg-bg-canvas px-3 py-2 text-sm text-fg-primary"
            />
          </div>
          <div>
            <label htmlFor="auth-5" className="block text-xs font-semibold text-fg-secondary">
              {copy.phoneLabel}
            </label>
            <input id="auth-5"
              type="tel"
              required
              value={phone}
              onChange={(e) => setPhone(e.target.value)}
              placeholder="+8801711000000"
              className="mt-1 w-full rounded-md border border-border-subtle bg-bg-canvas px-3 py-2 font-mono text-sm text-fg-primary"
            />
          </div>
          <div>
            <label htmlFor="auth-6" className="block text-xs font-semibold text-fg-secondary">
              {copy.passwordLabel}
            </label>
            <input id="auth-6"
              type="password"
              required
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              placeholder="Min 8 chars, Upper, Lower, Digit, Symbol"
              className="mt-1 w-full rounded-md border border-border-subtle bg-bg-canvas px-3 py-2 text-sm text-fg-primary"
            />
          </div>

          <button
            type="submit"
            disabled={submitting}
            className="w-full rounded-md bg-brand-primary px-4 py-2.5 text-sm font-semibold text-fg-on-accent transition-colors hover:bg-brand-primary-hover disabled:cursor-not-allowed disabled:opacity-60"
          >
            {copy.signUpButton}
          </button>

          <div className="pt-2 text-center">
            <button
              type="button"
              onClick={() => setMode('signin')}
              className="text-xs font-semibold text-brand-primary hover:underline"
            >
              {copy.switchToSignIn}
            </button>
          </div>
        </form>
      )}

      {mode === 'otp' && (
        <form onSubmit={handleVerifyOtpSubmit} className="mt-5 space-y-4">
          <div className="rounded-md border border-border-subtle bg-bg-elevated p-3 font-mono text-xs text-fg-secondary">
            Bound Reference ID: <span className="font-bold text-fg-primary">{referenceId}</span>
          </div>
          <div>
            <label htmlFor="auth-7" className="block text-xs font-semibold text-fg-secondary">
              {copy.otpCodeLabel}
            </label>
            <input id="auth-7"
              type="text"
              required
              maxLength={6}
              value={otpCode}
              onChange={(e) => setOtpCode(e.target.value)}
              placeholder="654321"
              className="mt-1 w-full rounded-md border border-border-subtle bg-bg-canvas px-3 py-2 text-center font-mono text-lg tracking-widest text-fg-primary"
            />
          </div>
          <button
            type="submit"
            disabled={submitting}
            className="w-full rounded-md bg-brand-primary px-4 py-2.5 text-sm font-semibold text-fg-on-accent disabled:cursor-not-allowed disabled:opacity-60"
          >
            {copy.verifyOtpButton}
          </button>
        </form>
      )}

      {(mode === 'forgot' || mode === 'reset') && (
        <form
          onSubmit={handleRecoverySubmit}
          className="mt-5 space-y-4"
        >
          <div>
            <label htmlFor="auth-8" className="block text-xs font-semibold text-fg-secondary">
              {copy.emailOrPhoneLabel}
            </label>
            <input id="auth-8"
              type="text"
              required
              value={identifier}
              onChange={(e) => setIdentifier(e.target.value)}
              className="mt-1 w-full rounded-md border border-border-subtle bg-bg-canvas px-3 py-2 text-sm text-fg-primary"
            />
          </div>
          {mode === 'reset' && (
            <>
              <div>
                <label htmlFor="auth-9" className="block text-xs font-semibold text-fg-secondary">
                  {copy.otpCodeLabel}
                </label>
                <input id="auth-9"
                  type="text"
                  required
                  value={otpCode}
                  onChange={(e) => setOtpCode(e.target.value)}
                  placeholder="6-digit OTP"
                  className="mt-1 w-full rounded-md border border-border-subtle bg-bg-canvas px-3 py-2 font-mono text-sm text-fg-primary"
                />
              </div>
              <div>
                <label htmlFor="auth-10" className="block text-xs font-semibold text-fg-secondary">
                  New {copy.passwordLabel}
                </label>
                <input id="auth-10"
                  type="password"
                  required
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  className="mt-1 w-full rounded-md border border-border-subtle bg-bg-canvas px-3 py-2 text-sm text-fg-primary"
                />
              </div>
            </>
          )}
          <button
            type="submit"
            disabled={submitting}
            className="w-full rounded-md bg-brand-primary px-4 py-2.5 text-sm font-semibold text-fg-on-accent disabled:cursor-not-allowed disabled:opacity-60"
          >
            {mode === 'forgot' ? 'Send Recovery OTP' : 'Verify OTP & Reset Password'}
          </button>
          <div className="pt-2 text-center">
            <button
              type="button"
              onClick={() => setMode('signin')}
              className="text-xs font-semibold text-brand-primary hover:underline"
            >
              {copy.switchToSignIn}
            </button>
          </div>
        </form>
      )}

      <div className="mt-5 rounded-md border border-border-subtle bg-bg-elevated p-3 text-xs text-fg-muted">
        {copy.inMemoryTokenNotice}
      </div>
    </div>
  );

  if (!isModal) {
    return containerContent;
  }

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-label={copy.signInTitle}
      className="fixed inset-0 z-50 flex items-center justify-center bg-bg-inverse/75 p-4 backdrop-blur-xs"
    >
      {containerContent}
    </div>
  );
}
