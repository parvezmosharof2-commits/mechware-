'use client';

import * as React from 'react';

export type ConsoleRole = 'admin' | 'finance' | 'moderator' | 'support' | 'seller';

export interface ConsoleNavItem {
  id: string;
  label: string;
  requiredPermission: string;
  allowedRoles: ConsoleRole[];
  badge?: string;
}

export const CONSOLE_NAV_ITEMS: ConsoleNavItem[] = [
  {
    id: 'seller-workspace',
    label: 'Seller Isolated Catalog & SubOrders',
    requiredPermission: 'sellers.manage_own',
    allowedRoles: ['seller', 'admin'],
    badge: 'TENANT ISOLATED'
  },
  {
    id: 'orders-refund',
    label: 'SubOrder Escrow & Refunds',
    requiredPermission: 'orders.refund',
    allowedRoles: ['support', 'finance', 'admin']
  },
  {
    id: 'catalog-moderate',
    label: 'Machinery Spec Moderation',
    requiredPermission: 'catalog.moderate',
    allowedRoles: ['moderator', 'admin']
  },
  {
    id: 'sellers-kyc',
    label: 'Seller Trade License & TIN KYC',
    requiredPermission: 'sellers.kyc_review',
    allowedRoles: ['moderator', 'admin']
  },
  {
    id: 'finance-payouts',
    label: 'Double-Entry Ledger & Payouts',
    requiredPermission: 'finance.payouts',
    allowedRoles: ['finance', 'admin'],
    badge: '2FA REQUIRED'
  },
  {
    id: 'users-rbac',
    label: 'Users, Roles & Session Families',
    requiredPermission: 'users.manage',
    allowedRoles: ['admin']
  },
  {
    id: 'audit-logs',
    label: 'EF Core Immutable Audit Trail',
    requiredPermission: 'audit.view',
    allowedRoles: ['finance', 'admin']
  }
];

export function getVisibleConsoleNavItems(role: ConsoleRole): ConsoleNavItem[] {
  return CONSOLE_NAV_ITEMS.filter((item) => item.allowedRoles.includes(role));
}

export interface RoleAwareSidebarProps {
  activeRole: ConsoleRole;
  activeItemId?: string;
  sellerId?: string;
  onSelectRole?: (role: ConsoleRole) => void;
  onSelectItem?: (itemId: string) => void;
}

export function RoleAwareSidebar({
  activeRole,
  activeItemId = 'orders-refund',
  sellerId = 'aaaa0000-0000-0000-0000-000000000001',
  onSelectRole,
  onSelectItem
}: RoleAwareSidebarProps) {
  const visibleItems = getVisibleConsoleNavItems(activeRole);
  const requires2Fa = activeRole === 'admin' || activeRole === 'finance';

  return (
    <aside
      aria-label="Role-aware console navigation"
      className="w-72 shrink-0 border-r border-border-subtle bg-bg-surface p-4 flex flex-col justify-between"
    >
      <div className="space-y-5">
        <div className="rounded-lg border border-border-subtle bg-bg-elevated p-3 space-y-2">
          <div className="flex items-center justify-between">
            <span className="text-xs font-semibold uppercase tracking-wider text-fg-muted">
              Active Staff/Seller Role
            </span>
            {requires2Fa && (
              <span className="rounded-sm bg-bg-surface px-1.5 py-0.5 font-mono text-xs font-bold text-brand-accent">
                MANDATORY 2FA
              </span>
            )}
          </div>

          {onSelectRole && (
            <select
              aria-label="Switch console RBAC role"
              value={activeRole}
              onChange={(e) => onSelectRole(e.target.value as ConsoleRole)}
              className="w-full rounded-md border border-border-subtle bg-bg-surface px-2.5 py-1.5 font-mono text-xs text-fg-primary"
            >
              <option value="admin">admin (All Permissions + 2FA)</option>
              <option value="finance">finance (Payouts + Refunds + 2FA)</option>
              <option value="moderator">moderator (Catalog + KYC Review)</option>
              <option value="support">support (Order Refunds)</option>
              <option value="seller">seller (Isolated SellerId Only)</option>
            </select>
          )}

          {activeRole === 'seller' && (
            <div className="rounded-sm border border-border-subtle bg-bg-surface p-2 font-mono text-xs text-fg-muted">
              Isolated SellerId: <span className="text-brand-accent">{sellerId.slice(0, 13)}…</span>
            </div>
          )}
        </div>

        <nav className="space-y-1">
          {visibleItems.map((item) => {
            const isActive = item.id === activeItemId;
            return (
              <button
                key={item.id}
                type="button"
                onClick={() => onSelectItem && onSelectItem(item.id)}
                className={`w-full rounded-md px-3 py-2.5 text-left text-xs transition-colors flex flex-col gap-1 ${
                  isActive
                    ? 'border border-brand-accent bg-bg-elevated font-semibold text-brand-accent'
                    : 'text-fg-secondary hover:bg-bg-elevated hover:text-fg-primary'
                }`}
              >
                <div className="flex items-center justify-between gap-2">
                  <span>{item.label}</span>
                  {item.badge && (
                    <span className="rounded-sm bg-bg-surface px-1.5 py-0.5 font-mono text-xs text-brand-accent">
                      {item.badge}
                    </span>
                  )}
                </div>
                <span className="font-mono text-xs text-fg-muted">
                  [{item.requiredPermission}]
                </span>
              </button>
            );
          })}
        </nav>
      </div>

      <div className="rounded-lg border border-border-subtle bg-bg-elevated p-3 text-xs text-fg-muted">
        <div className="font-semibold text-fg-primary">RBAC Policy Enforcement</div>
        <p className="mt-1">
          Routes are guarded by <code className="font-mono text-brand-accent">[RequirePermission]</code> &{' '}
          <code className="font-mono text-brand-accent">SellerResourceIsolationHandler</code>.
        </p>
      </div>
    </aside>
  );
}
