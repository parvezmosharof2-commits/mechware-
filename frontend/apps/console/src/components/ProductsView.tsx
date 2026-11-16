'use client';

import * as React from 'react';
import Link from 'next/link';
import { Badge, Button, Input, Skeleton } from '@mechware/ui';
import { PRODUCT_LIST_PAGE_SIZE, PRODUCT_STATUS_FILTERS, SUPPLY_FILTERS, formatTaka, productStatusLabel, statusBadgeVariant, supplyStatusLabel } from '@mechware/api-client';
import { catalogAdminApi, catalogError } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { BrandLookupPicker, CategoryLookupPicker, type Picked } from './LookupPicker';
import { ConsoleShell } from './ConsoleShell';
import { NoAccess } from './ShippingZonesView';

const SELECT = 'block w-full rounded-md border border-surface-border bg-surface p-2 text-sm';

function price(min: number | null, max: number | null): string {
  if (min === null || max === null) return 'No price';
  return min === max ? formatTaka(min) : `${formatTaka(min)} to ${formatTaka(max)}`;
}

function Products() {
  const [draft, setDraft] = React.useState('');
  const [q, setQ] = React.useState('');
  const [page, setPage] = React.useState(1);
  const [status, setStatus] = React.useState('');
  const [supply, setSupply] = React.useState('');
  const [brand, setBrand] = React.useState<Picked | null>(null);
  const [category, setCategory] = React.useState<Picked | null>(null);
  const filters = { q, status, supplyStatus: supply, brandId: brand?.id, categoryId: category?.id, page };
  const res = useResource(() => catalogAdminApi.productsList(filters), JSON.stringify(filters));
  const data = res.data?.data;
  const pages = data ? Math.max(1, Math.ceil(data.totalCount / PRODUCT_LIST_PAGE_SIZE)) : 1;
  const failure = res.error ? catalogError(res.error) : null;
  const narrow = <T,>(set: (v: T) => void) => (v: T) => { setPage(1); set(v); };
  const filtered = !!(q || status || supply || brand || category);

  return (
    <div className="space-y-5">
      <form className="flex flex-wrap items-end gap-3" role="search" onSubmit={(e) => { e.preventDefault(); setPage(1); setQ(draft.trim()); }}>
        <div className="min-w-0 flex-1 basis-64">
          <Input label="Search title, SKU or part number" type="search" value={draft} onChange={(e) => setDraft(e.target.value)} autoComplete="off" />
        </div>
        <Button type="submit" disabled={res.loading}>Search</Button>
        <div className="basis-40">
          <label className="mb-1 block text-sm font-medium" htmlFor="status-filter">Status</label>
          <select id="status-filter" className={SELECT} value={status} onChange={(e) => narrow(setStatus)(e.target.value)}>
            {PRODUCT_STATUS_FILTERS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
          </select>
        </div>
        <div className="basis-40">
          <label className="mb-1 block text-sm font-medium" htmlFor="supply-filter">Supply status</label>
          <select id="supply-filter" className={SELECT} value={supply} onChange={(e) => narrow(setSupply)(e.target.value)}>
            {SUPPLY_FILTERS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
          </select>
        </div>
      </form>
      <div className="grid gap-4 sm:grid-cols-2">
        <BrandLookupPicker idPrefix="filter" label="Filter by brand" value={brand} onChange={narrow(setBrand)} />
        <CategoryLookupPicker idPrefix="filter" label="Filter by category" value={category} onChange={narrow(setCategory)} clearable />
      </div>

      {failure && (
        <div role="alert" className="flex flex-wrap items-center gap-3 rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">
          <span>{failure.text}</span>
          {failure.retry && <Button type="button" variant="outline" size="sm" onClick={res.reload}>Try again</Button>}
        </div>
      )}
      {res.loading && !data && <div role="status"><span className="sr-only">Loading</span><Skeleton className="h-40 w-full" /></div>}
      {data && (data.items.length === 0 ? (
        <p role="status" data-testid="empty" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">
          {filtered ? 'No product matches these filters.' : 'No products yet.'}
        </p>
      ) : (
        <div className="overflow-x-auto rounded-md border border-surface-border">
          <table className="w-full text-left text-sm" data-testid="product-table" aria-busy={res.loading}>
            <caption className="sr-only">Products</caption>
            <thead className="bg-bg-subtle text-xs text-text-muted">
              <tr>
                <th scope="col" className="px-3 py-2">Title</th>
                <th scope="col" className="px-3 py-2">Brand</th>
                <th scope="col" className="px-3 py-2">Category</th>
                <th scope="col" className="px-3 py-2">Seller</th>
                <th scope="col" className="px-3 py-2">Status</th>
                <th scope="col" className="px-3 py-2">Supply</th>
                <th scope="col" className="px-3 py-2 text-right">SKUs</th>
                <th scope="col" className="px-3 py-2 text-right">Price (VAT incl.)</th>
                <th scope="col" className="px-3 py-2">Updated</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((r) => (
                <tr key={r.id} className="border-t border-surface-border" data-testid="product-row">
                  <td className="px-3 py-2">
                    <Link className="font-semibold text-brand-accent underline" href={`/catalog/products/${r.id}`}>{r.titleEn}</Link>
                    <span className="block text-xs text-text-muted">{r.titleBn}</span>
                  </td>
                  <td className="px-3 py-2">{r.brandName ?? <span className="text-text-muted">None</span>}</td>
                  <td className="px-3 py-2 text-xs">{r.categoryName}<span className="block text-text-muted">{r.categoryPath.replaceAll('.', ' / ')}</span></td>
                  <td className="px-3 py-2">{r.sellerStoreName}</td>
                  <td className="px-3 py-2"><Badge variant={statusBadgeVariant(r.status)}>{productStatusLabel(r.status)}</Badge></td>
                  <td className="px-3 py-2">{supplyStatusLabel(r.supplyStatus)}</td>
                  <td className="px-3 py-2 text-right">{r.variantCount}</td>
                  <td className="px-3 py-2 text-right font-mono whitespace-nowrap">{price(r.minPriceMinor, r.maxPriceMinor)}</td>
                  <td className="px-3 py-2 whitespace-nowrap text-xs">{r.updatedAtUtc ? r.updatedAtUtc.slice(0, 10) : ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ))}
      {data && (
        <nav aria-label="Pages" className="flex items-center gap-3 text-sm">
          <Button type="button" variant="outline" size="sm" disabled={page <= 1 || res.loading} onClick={() => setPage(page - 1)}>Previous</Button>
          <span data-testid="page-info">Page {page} of {pages} ({data.totalCount} products)</span>
          <Button type="button" variant="outline" size="sm" disabled={page >= pages || res.loading} onClick={() => setPage(page + 1)}>Next</Button>
        </nav>
      )}
    </div>
  );
}

export function ProductsView() {
  const { user } = useAuth();
  const held = new Set((user?.permissions ?? []).map((p) => p.toLowerCase()));
  return (
    <ConsoleShell activeItemId="catalog-products" title="Products">
      {held.has('catalog.manage') ? <Products /> : <NoAccess permission="catalog.manage" />}
    </ConsoleShell>
  );
}
