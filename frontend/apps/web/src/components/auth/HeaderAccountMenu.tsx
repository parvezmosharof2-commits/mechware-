'use client';

import * as React from 'react';
import { defaultTokenVault } from '@mechware/api-client';

export interface HeaderAccountMenuProps {
  locale?: 'en' | 'bn';
  user?: {
    fullName: string;
    email: string;
    roles: string[];
    twoFactorEnabled: boolean;
  } | null;
  onOpenSignInModal?: () => void;
  onSignOut?: () => void;
}

export function HeaderAccountMenu({
  locale = 'en',
  user,
  onOpenSignInModal,
  onSignOut
}: HeaderAccountMenuProps) {
  const [open, setOpen] = React.useState(false);

  if (!user) {
    return (
      <button
        type="button"
        onClick={onOpenSignInModal}
        className="rounded-md bg-brand-primary px-4 py-2 text-xs font-semibold text-fg-on-accent transition-colors hover:bg-brand-primary-hover"
      >
        {locale === 'bn' ? 'সাইন ইন / নিবন্ধন' : 'Sign In / Register'}
      </button>
    );
  }

  const handleSignOutClick = () => {
    defaultTokenVault.clear();
    setOpen(false);
    if (onSignOut) {
      onSignOut();
    }
  };

  return (
    <div className="relative inline-block text-left">
      <button
        type="button"
        aria-expanded={open}
        aria-haspopup="menu"
        onClick={() => setOpen((prev) => !prev)}
        className="flex items-center gap-2 rounded-md border border-border-subtle bg-bg-surface px-3 py-2 text-xs font-semibold text-fg-primary hover:bg-bg-elevated"
      >
        <span className="inline-block h-2 w-2 rounded-full bg-state-success" />
        <span>{user.fullName}</span>
        <span className="rounded-sm bg-bg-elevated px-1.5 py-0.5 font-mono text-xs uppercase text-brand-accent">
          {user.roles[0] ?? 'buyer'}
        </span>
      </button>

      {open && (
        <div
          role="menu"
          className="absolute right-0 z-40 mt-2 w-72 rounded-md border border-border-subtle bg-bg-surface p-4 text-xs text-fg-primary shadow-lg"
        >
          <div className="border-b border-border-subtle pb-3">
            <div className="font-semibold text-fg-primary">{user.fullName}</div>
            <div className="font-mono text-fg-muted">{user.email}</div>
            <div className="mt-2 flex flex-wrap gap-1">
              {user.roles.map((r) => (
                <span
                  key={r}
                  className="rounded-sm border border-border-subtle bg-bg-elevated px-2 py-0.5 font-mono uppercase text-brand-accent"
                >
                  {r}
                </span>
              ))}
              {user.twoFactorEnabled && (
                <span className="rounded-sm border border-state-success bg-bg-elevated px-2 py-0.5 font-mono text-state-success">
                  2FA TOTP ACTIVE
                </span>
              )}
            </div>
          </div>

          <div className="py-2 space-y-1">
            <a
              href={`/${locale}/account/addresses`}
              className="block rounded-sm px-2 py-1.5 text-fg-secondary hover:bg-bg-elevated hover:text-fg-primary"
            >
              {locale === 'bn' ? 'বাংলাদেশ ডেলিভারি ঠিকানা (Address Book)' : 'Bangladesh Address Book'}
            </a>
            <a
              href={`/${locale}/account/company`}
              className="block rounded-sm px-2 py-1.5 text-fg-secondary hover:bg-bg-elevated hover:text-fg-primary"
            >
              {locale === 'bn' ? 'কোম্পানি অ্যাকাউন্ট' : 'Company account'}
            </a>
            <button
              type="button"
              onClick={handleSignOutClick}
              className="w-full rounded-sm px-2 py-1.5 text-left font-semibold text-state-danger hover:bg-bg-elevated"
            >
              {locale === 'bn' ? 'সেশন বাতিল ও সাইন আউট' : 'Revoke Session & Sign Out'}
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
