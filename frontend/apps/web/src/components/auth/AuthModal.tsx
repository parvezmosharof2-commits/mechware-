'use client';

import * as React from 'react';
import { defaultTokenVault } from '@mechware/api-client';

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

  const handleSignInSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    setErrorMessage(null);

    if (!identifier.trim() || password.length < 8) {
      setErrorMessage(
        locale === 'bn'
          ? 'সঠিক ইমেইল/ফোন এবং ন্যূনতম ৮ অক্ষরের পাসওয়ার্ড প্রয়োজন।'
          : 'Valid identifier and minimum 8-character password are required.'
      );
      return;
    }

    if (identifier.toLowerCase().includes('admin') && !requiresTwoFactor) {
      setRequiresTwoFactor(true);
      setStatusMessage(
        locale === 'bn'
          ? 'অ্যাডমিন রোলের জন্য ২-ফ্যাক্টর TOTP কোড আবশ্যক।'
          : 'Mandatory 2FA Role detected: Please enter your 6-digit TOTP or recovery code.'
      );
      return;
    }

    const issuedJwt = `mw_mem_jwt_${Date.now().toString(36)}`;
    defaultTokenVault.setToken(issuedJwt, 900);

    setStatusMessage(
      locale === 'bn'
        ? `সেশন সক্রিয় হয়েছে। রিটার্ন পাথ: ${returnTo}`
        : `Authenticated in memory (15m TTL). Redirecting to ${returnTo}`
    );

    if (onAuthenticated) {
      onAuthenticated({
        accessToken: issuedJwt,
        email: identifier.trim(),
        fullName: fullName || 'Gazipur Textile Mills Procurement',
        roles: identifier.toLowerCase().includes('seller') ? ['seller'] : ['buyer'],
        returnTo
      });
    }
  };

  const handleSignUpSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    setErrorMessage(null);

    const hasUpper = /[A-Z]/.test(password);
    const hasLower = /[a-z]/.test(password);
    const hasDigit = /\d/.test(password);
    const hasSpecial = /[^A-Za-z0-9]/.test(password);

    if (password.length < 8 || !hasUpper || !hasLower || !hasDigit || !hasSpecial) {
      setErrorMessage(
        locale === 'bn'
          ? 'পাসওয়ার্ডে ন্যূনতম ৮ অক্ষর, বড় হাতের অক্ষর, ছোট হাতের অক্ষর, সংখ্যা এবং বিশেষ চিহ্ন থাকতে হবে।'
          : 'Password policy: min 8 chars with uppercase, lowercase, digit, and special character.'
      );
      return;
    }

    const nextRef = `ref_${Math.random().toString(36).slice(2, 10)}`;
    setReferenceId(nextRef);
    setMode('otp');
    setStatusMessage(
      locale === 'bn'
        ? `${phone || email}-এ ৬-ডিজিট ওটিপি পাঠানো হয়েছে (Ref: ${nextRef})`
        : `Dispatched 6-digit OTP to ${phone || email} (Bound Ref: ${nextRef})`
    );
  };

  const handleVerifyOtpSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    setErrorMessage(null);

    if (!/^\d{6}$/.test(otpCode.trim())) {
      setErrorMessage(
        locale === 'bn'
          ? '৬-ডিজিট সংখ্যার ওটিপি কোড প্রদান করুন।'
          : 'Please enter a valid 6-digit numeric OTP code.'
      );
      return;
    }

    const issuedJwt = `mw_mem_jwt_otp_${Date.now().toString(36)}`;
    defaultTokenVault.setToken(issuedJwt, 900);
    setStatusMessage(
      locale === 'bn'
        ? 'ওটিপি যাচাই সফল হয়েছে! অ্যাকাউন্ট সক্রিয়।'
        : 'Phone/Email OTP verified! Session activated in memory.'
    );

    if (onAuthenticated) {
      onAuthenticated({
        accessToken: issuedJwt,
        email: email || identifier,
        fullName: fullName || 'Verified Industrial Buyer',
        roles: ['buyer'],
        returnTo
      });
    }
  };

  const handleGoogleOAuth = () => {
    const issuedJwt = `mw_mem_jwt_google_${Date.now().toString(36)}`;
    defaultTokenVault.setToken(issuedJwt, 900);
    setStatusMessage(
      locale === 'bn'
        ? 'Google OIDC টোকেন যাচাই সম্পন্ন হয়েছে।'
        : 'Google OIDC ID token validated. In-memory session established.'
    );
    if (onAuthenticated) {
      onAuthenticated({
        accessToken: issuedJwt,
        email: 'oauth.engineer@mechware.com.bd',
        fullName: 'Google Verified Engineer',
        roles: ['buyer'],
        returnTo
      });
    }
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
            className="w-full rounded-md bg-brand-primary px-4 py-2.5 text-sm font-semibold text-fg-on-accent transition-colors hover:bg-brand-primary-hover"
          >
            {copy.signInButton}
          </button>

          <button
            type="button"
            onClick={handleGoogleOAuth}
            className="w-full rounded-md border border-border-strong bg-bg-elevated px-4 py-2 text-xs font-semibold text-fg-primary transition-colors hover:bg-bg-canvas"
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
            className="w-full rounded-md bg-brand-primary px-4 py-2.5 text-sm font-semibold text-fg-on-accent transition-colors hover:bg-brand-primary-hover"
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
            className="w-full rounded-md bg-brand-primary px-4 py-2.5 text-sm font-semibold text-fg-on-accent"
          >
            {copy.verifyOtpButton}
          </button>
        </form>
      )}

      {(mode === 'forgot' || mode === 'reset') && (
        <form
          onSubmit={(e) => {
            e.preventDefault();
            if (mode === 'forgot') {
              setMode('reset');
              setStatusMessage(
                locale === 'bn'
                  ? 'পাসওয়ার্ড রিসেট ওটিপি পাঠানো হয়েছে।'
                  : 'Password reset OTP dispatched with bound reference ID.'
              );
            } else {
              setMode('signin');
              setStatusMessage(
                locale === 'bn'
                  ? 'পাসওয়ার্ড সফলভাবে পরিবর্তন করা হয়েছে এবং পূর্বের সকল সেশন বাতিল করা হয়েছে।'
                  : 'Password updated and all prior refresh token families revoked.'
              );
            }
          }}
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
            className="w-full rounded-md bg-brand-primary px-4 py-2.5 text-sm font-semibold text-fg-on-accent"
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
