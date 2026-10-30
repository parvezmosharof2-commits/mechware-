'use client';

import * as React from 'react';
import Link from 'next/link';
import { ConsoleShell } from '../components/ConsoleShell';
import { getVisibleConsoleNavItems } from '../components/RoleAwareSidebar';
import { useAuth } from '../lib/auth';

export default function ConsoleHomePage() {
  const { user } = useAuth();
  const modules = user ? getVisibleConsoleNavItems(user.permissions).filter((m) => m.href) : [];

  return (
    <ConsoleShell title="Operations console">
      <div className="space-y-4 rounded-xl border border-surface-border bg-surface p-6">
        <p className="text-sm text-text-muted">
          Signed in as <span className="font-semibold text-text">{user?.fullName}</span> ({user?.roles.join(', ')}).
        </p>
        {modules.length === 0 ? (
          <p className="text-sm text-text-muted">No console modules are available for your role yet.</p>
        ) : (
          <ul className="space-y-2">
            {modules.map((m) => (
              <li key={m.id}>
                <Link href={m.href!} className="text-sm font-semibold text-accent hover:underline">
                  {m.label}
                </Link>
              </li>
            ))}
          </ul>
        )}
      </div>
    </ConsoleShell>
  );
}
