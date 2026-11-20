import Link from 'next/link';

/** The two order lists, side by side. The current one is named in words (aria-current) as well as shown in bold. */
export function OrdersNav({ current }: { current: 'all' | 'overdue' }) {
  const items = [{ id: 'all', href: '/orders', label: 'All orders' }, { id: 'overdue', href: '/orders/overdue', label: 'Overdue orders' }] as const;
  return (
    <nav aria-label="Order lists" className="flex flex-wrap gap-2 text-sm">
      {items.map((i) => (
        <Link key={i.id} href={i.href} aria-current={i.id === current ? 'page' : undefined}
          className={`inline-flex min-h-11 items-center rounded-md border px-4 font-semibold ${i.id === current ? 'border-brand-accent bg-bg-subtle' : 'border-surface-border text-brand-accent underline'}`}>
          {i.label}
        </Link>
      ))}
    </nav>
  );
}
