'use client';

import * as React from 'react';
import { HeaderAccountMenu } from '../auth/HeaderAccountMenu';
import { useAuth } from '../../lib/auth';

export function HeaderAccountLive({ locale }: { locale: 'en' | 'bn' }) {
  const { user, signOut } = useAuth();
  const signIn = () => {
    const returnTo = `${window.location.pathname}${window.location.search}`;
    const base = locale === 'bn' ? '/bn/auth/login' : '/auth/login';
    window.location.assign(`${base}?returnTo=${encodeURIComponent(returnTo)}`);
  };
  return (
    <HeaderAccountMenu
      locale={locale}
      user={user && { fullName: user.fullName, email: user.email, roles: user.roles, twoFactorEnabled: user.twoFactorEnabled }}
      onOpenSignInModal={signIn}
      onSignOut={() => void signOut()}
    />
  );
}
