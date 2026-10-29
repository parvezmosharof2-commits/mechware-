'use client';

import * as React from 'react';
import { ConsoleRole, RoleAwareSidebar } from '../components/RoleAwareSidebar';

export default function ConsoleDashboardPage() {
  const [activeRole, setActiveRole] = React.useState<ConsoleRole>('admin');
  const [activeItemId, setActiveItemId] = React.useState<string>('orders-refund');

  return (
    <div className="flex min-h-screen flex-col">
      <header className="border-b border-surface-border bg-bg-subtle px-6 py-4 flex items-center justify-between">
        <div className="flex items-center gap-3">
          <div className="w-8 h-8 rounded bg-brand-navy flex items-center justify-center font-bold text-accent">
            MW
          </div>
          <span className="font-display font-bold text-lg tracking-wide">
            MECHWARE <span className="text-accent text-sm font-normal">Console · Phase 3 RBAC</span>
          </span>
        </div>
        <div className="flex items-center gap-4 text-sm text-text-muted">
          <span>Parvez Mosharof ({activeRole.toUpperCase()})</span>
          <span className="w-2 h-2 rounded-full bg-status-online" />
        </div>
      </header>

      <div className="flex flex-1">
        <RoleAwareSidebar
          activeRole={activeRole}
          activeItemId={activeItemId}
          onSelectRole={setActiveRole}
          onSelectItem={setActiveItemId}
        />

        <main className="flex-1 p-6 space-y-6 max-w-7xl mx-auto w-full">
          <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
            <div className="p-5 rounded-xl border border-surface-border bg-surface">
              <div className="text-sm text-text-muted">Total Gross Volume</div>
              <div className="text-2xl font-bold font-mono text-accent pt-1">৳1,48,50,000</div>
              <div className="text-xs text-status-online pt-1">↑ 14.2% vs last month</div>
            </div>
            <div className="p-5 rounded-xl border border-surface-border bg-surface">
              <div className="text-sm text-text-muted">Active SubOrders</div>
              <div className="text-2xl font-bold font-mono text-text pt-1">42</div>
              <div className="text-xs text-text-muted pt-1">12 pending shipment</div>
            </div>
            <div className="p-5 rounded-xl border border-surface-border bg-surface">
              <div className="text-sm text-text-muted">Sellers & Distributors</div>
              <div className="text-2xl font-bold font-mono text-text pt-1">25</div>
              <div className="text-xs text-status-online pt-1">Seller Isolation Active</div>
            </div>
            <div className="p-5 rounded-xl border border-surface-border bg-surface">
              <div className="text-sm text-text-muted">Token Security</div>
              <div className="text-2xl font-bold text-status-online pt-1">15m / 7d</div>
              <div className="text-xs text-text-muted pt-1">In-Memory JWT + httpOnly Cookie</div>
            </div>
          </div>

          <div className="p-6 rounded-xl border border-surface-border bg-surface space-y-4">
            <h2 className="text-lg font-bold font-display">
              Phase 3 Identity, Token Family Tracking & Seller Resource Isolation Ready
            </h2>
            <p className="text-sm text-text-muted leading-relaxed">
              Active Console Module: <span className="font-mono text-accent">{activeItemId}</span> · Role:{' '}
              <span className="font-mono text-accent">{activeRole}</span>. EF Core AuditLogSaveChangesInterceptor
              automatically records OldValues, NewValues, ActorId, and IP on every state mutation.
            </p>
          </div>
        </main>
      </div>
    </div>
  );
}
