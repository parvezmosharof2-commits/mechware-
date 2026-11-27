'use client';

import * as React from 'react';
import Link from 'next/link';
import { usePendingApprovals } from '../lib/usePendingApprovals';

export interface ConsoleNavItem {
  id: string;
  label: string;
  requiredPermission: string;
  /** Route of a built page. Items without one are planned modules and render as disabled. */
  href?: string;
  badge?: string;
  /** Heading shown above the first visible item of each group. */
  group?: string;
}

export const CONSOLE_NAV_ITEMS: ConsoleNavItem[] = [
  {
    id: 'companies',
    label: 'B2B Companies & Credit',
    requiredPermission: 'companies.view',
    href: '/companies'
  },
  {
    id: 'orders-all',
    label: 'All orders',
    requiredPermission: 'orders.view_all',
    href: '/orders',
    group: 'Orders'
  },
  {
    id: 'orders-overdue',
    label: 'Overdue orders',
    requiredPermission: 'orders.view_all',
    href: '/orders/overdue',
    group: 'Orders'
  },
  {
    id: 'returns',
    label: 'Returns',
    requiredPermission: 'orders.view_all',
    href: '/returns',
    group: 'Orders'
  },
  {
    id: 'orders-refund',
    label: 'Refunds',
    requiredPermission: 'orders.refund',
    href: '/refunds',
    group: 'Orders'
  },
  {
    id: 'invoices',
    label: 'Invoices',
    requiredPermission: 'invoices.view',
    href: '/invoices',
    group: 'Orders'
  },
  {
    id: 'credit-notes',
    label: 'Credit notes',
    requiredPermission: 'invoices.view',
    href: '/invoices/credit-notes',
    group: 'Orders'
  },
  {
    id: 'vat-month',
    label: 'Monthly VAT file',
    requiredPermission: 'invoices.export',
    href: '/invoices/vat-month',
    group: 'Orders'
  },
  {
    id: 'quotes-rfqs',
    label: 'RFQ inbox',
    requiredPermission: 'quotes.view',
    href: '/quotes/rfqs',
    group: 'Quotes'
  },
  {
    id: 'quotes-list',
    label: 'Quotes',
    requiredPermission: 'quotes.view',
    href: '/quotes',
    group: 'Quotes'
  },
  {
    id: 'quotes-approvals',
    label: 'Quote approvals',
    requiredPermission: 'quotes.approve',
    href: '/quotes/approvals',
    group: 'Quotes'
  },
  {
    id: 'quotes-terms',
    label: 'Quote terms',
    requiredPermission: 'quotes.view',
    href: '/quotes/terms',
    group: 'Quotes'
  },
  {
    id: 'quotes-new',
    label: 'New quote for a company',
    requiredPermission: 'quotes.edit',
    href: '/quotes/new',
    group: 'Quotes'
  },
  {
    id: 'catalog-products',
    label: 'Products',
    requiredPermission: 'catalog.manage',
    href: '/catalog/products',
    group: 'Catalogue'
  },
  {
    id: 'catalog-import',
    label: 'Bulk catalogue import',
    requiredPermission: 'catalog.manage',
    href: '/catalog/import',
    group: 'Catalogue'
  },
  {
    id: 'price-lists',
    label: 'Price lists',
    requiredPermission: 'pricelists.view',
    href: '/pricing/price-lists',
    group: 'Pricing'
  },
  {
    id: 'price-list-approvals',
    label: 'Pending approval',
    requiredPermission: 'pricelists.approve',
    href: '/pricing/price-lists/approvals',
    group: 'Pricing'
  },
  {
    id: 'margin-floors',
    label: 'Margin floors',
    requiredPermission: 'pricing.view_cost',
    href: '/pricing/margin-floors',
    group: 'Pricing'
  },
  {
    id: 'shipping-zones',
    label: 'Shipping zones',
    requiredPermission: 'shipping.view',
    href: '/shipping/zones',
    group: 'Shipping'
  },
  {
    id: 'credit-payments',
    label: 'Credit payments',
    requiredPermission: 'credit.view',
    href: '/credit/payments',
    group: 'Credit'
  },
  {
    id: 'credit-record',
    label: 'Record a payment',
    requiredPermission: 'credit.record_payment',
    href: '/credit/payments/new',
    group: 'Credit'
  },
  {
    id: 'credit-aging',
    label: 'Credit aging',
    requiredPermission: 'credit.view',
    href: '/credit/aging',
    group: 'Credit'
  },
  {
    id: 'credit-statement',
    label: 'Company credit books',
    requiredPermission: 'credit.view',
    href: '/credit/companies',
    group: 'Credit'
  },
  {
    id: 'credit-adjustments',
    label: 'Credit adjustments',
    requiredPermission: 'credit.view',
    href: '/credit/adjustments',
    group: 'Credit'
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
  { id: 'staff-2fa', label: 'Staff authenticator setup', requiredPermission: 'users.manage', href: '/staff', group: 'Staff' },
  { id: 'staff-users', label: 'Staff users', requiredPermission: 'users.manage', href: '/staff/users', group: 'Staff' },
  { id: 'staff-role-changes', label: 'Role change requests', requiredPermission: 'users.manage', href: '/staff/role-changes', group: 'Staff' },
  { id: 'staff-roles', label: 'Roles and permissions', requiredPermission: 'users.manage', href: '/staff/roles', group: 'Staff' },
  {
    id: 'audit-logs',
    label: 'Audit log integrity',
    requiredPermission: 'audit.view',
    href: '/audit'
  },
  {
    id: 'audit-log',
    label: 'Audit log',
    requiredPermission: 'audit.view',
    href: '/audit/log'
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
  const pendingCount = usePendingApprovals(permissions.some((p) => p.toLowerCase() === 'pricelists.approve'));
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
          {visibleItems.map((item, index) => {
            const heading = item.group && item.group !== visibleItems[index - 1]?.group ? item.group : null;
            const isActive = item.id === activeItemId;
            const body = (
              <>
                <div className="flex items-center justify-between gap-2">
                  <span>{item.label}</span>
                  {item.id === 'price-list-approvals' && pendingCount !== null && pendingCount > 0 && (
                    <span data-testid="pending-count" aria-label={`${pendingCount} waiting`} className="rounded-sm bg-bg-surface px-1.5 py-0.5 font-mono text-xs font-bold text-brand-accent">
                      {pendingCount}
                    </span>
                  )}
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
            const groupHeading = heading ? (
              <p key={`${item.id}-group`} className="px-3 pt-2 text-xs font-semibold uppercase tracking-wider text-fg-muted">{heading}</p>
            ) : null;
            if (!item.href) {
              return (
                <React.Fragment key={item.id}>
                  {groupHeading}
                  <div aria-disabled="true" className={`${base} cursor-not-allowed text-fg-muted opacity-70`}>
                    {body}
                  </div>
                </React.Fragment>
              );
            }
            return (
              <React.Fragment key={item.id}>
                {groupHeading}
              <Link
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
              </React.Fragment>
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
