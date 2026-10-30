'use client';

import * as React from 'react';
import Link from 'next/link';

export interface ConsoleNavItem {
  id: string;
  label: string;
  requiredPermission: string;
  /** Route of a built page. Items without one are planned modules and render as disabled. */
  href?: string;
  badge?: string;
}

export const CONSOLE_NAV_ITEMS: ConsoleNavItem[] = [
  {
    id: 'companies',
    label: 'B2B Companies & Credit',
    requiredPermission: 'companies.view',
    href: '/companies'
  },
  {
    id: 'orders-refund',
    label: 'SubOrder Escrow & Refunds',
    requiredPermission: 'orders.refund'
  },
  {
    id: 'catalog-moderate',
    label: 'Machinery Spec Moderation',
    requiredPermission: 'catalog.moderate'
  },
  {
    id: 'sellers-kyc',
    label: 'Seller Trade License & TIN KYC',
    requiredPermission: 'sellers.kyc_review'
  },
  {
    id: 'finance-payouts',
    label: 'Double-Entry Ledger & Payouts',
    requiredPermission: 'finance.payouts',
    badge: '2FA REQUIRED'
  },
  {
    id: 'users-rbac',
    label: 'Users, Roles & Session Families',
    requiredPermission: 'users.manage'
  },
  {
    id: 'audit-logs',
    label: 'EF Core Immutable Audit Trail',
    requiredPermission: 'audit.view'
  }
];

/** Items the signed-in user holds the permission for. The API still enforces every permission itself. */
export function getVisibleConsoleNavItems(permissions: readonly string[]): ConsoleNavItem[] {
  const held = new Set(permissions.map((p) => p.toLowerCase()));
  return CONSOLE_NAV_ITEMS.filter((item) => held.has(item.requiredPermission));
}

export interface RoleAwareSidebarProps {
  roles: readonly string[];
  permissions: readonly string[];
  activeItemId?: string;
}

export function RoleAwareSidebar({ roles, permissions, activeItemId }: RoleAwareSidebarProps) {
  const visibleItems = getVisibleConsoleNavItems(permissions);
  const requires2Fa = roles.some((r) => r === 'admin' || r === 'finance');

  return (
    <aside
      aria-label="Console navigation"
      className="w-full shrink-0 border-b border-border-subtle bg-bg-surface p-4 flex flex-col justify-between gap-4 md:w-72 md:border-b-0 md:border-r"
    >
      <div className="space-y-5">
        <div className="rounded-lg border border-border-subtle bg-bg-elevated p-3 space-y-2">
          <div className="flex items-center justify-between">
            <span className="text-xs font-semibold uppercase tracking-wider text-fg-muted">Your role</span>
            {requires2Fa && (
              <span className="rounded-sm bg-bg-surface px-1.5 py-0.5 font-mono text-xs font-bold text-brand-accent">
                2FA ENFORCED
              </span>
            )}
          </div>
          <div className="font-mono text-xs text-fg-primary">{roles.join(', ') || 'none'}</div>
        </div>

        <nav className="space-y-1" aria-label="Modules">
          {visibleItems.map((item) => {
            const isActive = item.id === activeItemId;
            const body = (
              <>
                <div className="flex items-center justify-between gap-2">
                  <span>{item.label}</span>
                  {item.badge && (
                    <span className="rounded-sm bg-bg-surface px-1.5 py-0.5 font-mono text-xs text-brand-accent">
                      {item.badge}
                    </span>
                  )}
                  {!item.href && (
                    <span className="rounded-sm bg-bg-surface px-1.5 py-0.5 font-mono text-xs text-fg-muted">SOON</span>
                  )}
                </div>
                <span className="font-mono text-xs text-fg-muted">[{item.requiredPermission}]</span>
              </>
            );
            const base = 'w-full rounded-md px-3 py-2.5 text-left text-xs transition-colors flex flex-col gap-1';
            if (!item.href) {
              return (
                <div key={item.id} aria-disabled="true" className={`${base} cursor-not-allowed text-fg-muted opacity-70`}>
                  {body}
                </div>
              );
            }
            return (
              <Link
                key={item.id}
                href={item.href}
                aria-current={isActive ? 'page' : undefined}
                className={`${base} ${
                  isActive
                    ? 'border border-brand-accent bg-bg-elevated font-semibold text-brand-accent'
                    : 'text-fg-secondary hover:bg-bg-elevated hover:text-fg-primary'
                }`}
              >
                {body}
              </Link>
            );
          })}
          {visibleItems.length === 0 && <p className="px-3 text-xs text-fg-muted">No console modules are available for your role yet.</p>}
        </nav>
      </div>

      <div className="rounded-lg border border-border-subtle bg-bg-elevated p-3 text-xs text-fg-muted">
        <div className="font-semibold text-fg-primary">Access is enforced by the API</div>
        <p className="mt-1">Hiding a link is only a convenience. Every action is checked again on the server.</p>
      </div>
    </aside>
  );
}
