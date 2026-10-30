'use client';

import * as React from 'react';
import { useRouter } from 'next/navigation';
import { Button, Skeleton } from '@mechware/ui';
import { useAuth } from '../lib/auth';
import { RoleAwareSidebar } from './RoleAwareSidebar';

/** Frame for every signed-in console page. Signed-out visitors are sent to /login. */
export function ConsoleShell({ activeItemId, title, children }: { activeItemId?: string; title: string; children: React.ReactNode }) {
  const { status, user, signOut } = useAuth();
  const router = useRouter();

  React.useEffect(() => {
    if (status === 'anonymous') router.replace('/login');
  }, [status, router]);

  if (status !== 'authenticated' || !user) {
    return (
      <div role="status" aria-live="polite" className="space-y-3 p-6">
        <span className="sr-only">Loading</span>
        <Skeleton className="h-12 w-full" />
        <Skeleton className="h-64 w-full" />
      </div>
    );
  }

  return (
    <div className="flex min-h-screen flex-col">
      <header className="flex flex-wrap items-center justify-between gap-3 border-b border-surface-border bg-bg-subtle px-6 py-4">
        <div className="flex items-center gap-3">
          <div className="flex h-8 w-8 items-center justify-center rounded bg-brand-navy font-bold text-accent">MW</div>
          <span className="font-display text-lg font-bold tracking-wide">
            MECHWARE <span className="text-sm font-normal text-accent">Console</span>
          </span>
        </div>
        <div className="flex items-center gap-4 text-sm text-text-muted">
          <span>{user.fullName}</span>
          <Button
            variant="outline"
            size="sm"
            onClick={async () => {
              await signOut();
              router.replace('/login');
            }}
          >
            Sign out
          </Button>
        </div>
      </header>

      <div className="flex flex-1 flex-col md:flex-row">
        <RoleAwareSidebar roles={user.roles} permissions={user.permissions} activeItemId={activeItemId} />
        <main className="mx-auto w-full min-w-0 max-w-7xl flex-1 space-y-6 p-4 md:p-6">
          <h1 className="font-display text-2xl font-bold">{title}</h1>
          {children}
        </main>
      </div>
    </div>
  );
}
