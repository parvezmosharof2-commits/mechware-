'use client';

import * as React from 'react';
import Link from 'next/link';
import { Badge, Button, Input, Skeleton } from '@mechware/ui';
import {
  PRODUCT_LIST_PAGE_SIZE, PRODUCT_STATUS_FILTERS, brandsOf, filterRows, groupVariantsByProduct, priceRange, productStatusLabel
} from '@mechware/api-client';
import { catalogError, lookupsApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';
import { NoAccess } from './ShippingZonesView';

const SELECT = 'block w-full rounded-md border border-surface-border bg-surface p-2 text-sm';

function statusVariant(s: string): 'success' | 'outline' | 'low-stock' {
  return s === 'active' ? 'success' : s === 'pending_review' ? 'low-stock' : 'outline';
}

function Products() {
  const [draft, setDraft] = React.useState('');
  const [q, setQ] = React.useState('');
  const [page, setPage] = React.useState(1);
  const [status, setStatus] = React.useState('');
  const [brand, setBrand] = React.useState('');
  const res = useResource(() => lookupsApi.variants({ q, page, pageSize: PRODUCT_LIST_PAGE_SIZE }), `${q}|${page}`);
  const data = res.data?.data;
  const rows = React.useMemo(() => groupVariantsByProduct(data?.items ?? []), [data]);
  const brands = brandsOf(rows);
  const shown = filterRows(rows, { status, brand: brands.includes(brand) ? brand : '' });
  const pages = data ? Math.max(1, Math.ceil(data.totalCount / PRODUCT_LIST_PAGE_SIZE)) : 1;
  const failure = res.error ? catalogError(res.error) : null;

  return (
    <div className="space-y-5">
      <form
        className="flex flex-wrap items-end gap-3"
        role="search"
        onSubmit={(e) => { e.preventDefault(); setPage(1); setQ(draft.trim()); }}
      >
        <div className="min-w-0 flex-1 basis-64">
          <Input label="Search title, SKU or part number" type="search" value={draft} onChange={(e) => setDraft(e.target.value)} autoComplete="off" />
        </div>
        <Button type="submit" disabled={res.loading}>Search</Button>
        <div className="basis-40">
          <label className="mb-1 block text-sm font-medium" htmlFor="status-filter">Status (this page)</label>
          <select id="status-filter" className={SELECT} value={status} onChange={(e) => setStatus(e.target.value)}>
            {PRODUCT_STATUS_FILTERS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
          </select>
        </div>
        <div className="basis-48">
          <label className="mb-1 block text-sm font-medium" htmlFor="brand-filter">Brand (this page)</label>
          <select id="brand-filter" className={SELECT} value={brands.includes(brand) ? brand : ''} onChange={(e) => setBrand(e.target.value)}>
            <option value="">Any brand</option>
            {brands.map((b) => <option key={b} value={b}>{b}</option>)}
          </select>
        </div>
      </form>
      <p className="text-xs text-text-muted">
        The product list comes from the SKU lookup, so the page holds up to {PRODUCT_LIST_PAGE_SIZE} SKUs grouped by product. The status and brand filters narrow the loaded page only.
      </p>

      {failure && (
        <div role="alert" className="flex flex-wrap items-center gap-3 rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">
          <span>{failure.text}</span>
          {failure.retry && <Button type="button" variant="outline" size="sm" onClick={res.reload}>Try again</Button>}
        </div>
      )}
      {res.loading && !data && <div role="status"><span className="sr-only">Loading</span><Skeleton className="h-40 w-full" /></div>}
      {data && (shown.length === 0 ? (
        <p role="status" data-testid="empty" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">
          {rows.length === 0 ? (q ? `No product matches "${q}".` : 'No products yet.') : 'No product on this page matches the filters.'}
        </p>
      ) : (
        <div className="overflow-x-auto rounded-md border border-surface-border">
          <table className="w-full text-left text-sm" data-testid="product-table" aria-busy={res.loading}>
            <caption className="sr-only">Products</caption>
            <thead className="bg-bg-subtle text-xs text-text-muted">
              <tr>
                <th scope="col" className="px-3 py-2">Title</th>
                <th scope="col" className="px-3 py-2">SKUs</th>
                <th scope="col" className="px-3 py-2">Brand</th>
                <th scope="col" className="px-3 py-2">Status</th>
                <th scope="col" className="px-3 py-2 text-right">List price (VAT incl.)</th>
              </tr>
            </thead>
            <tbody>
              {shown.map((r) => (
                <tr key={r.productId} className="border-t border-surface-border" data-testid="product-row">
                  <td className="px-3 py-2"><Link className="font-semibold text-brand-accent underline" href={`/catalog/products/${r.productId}`}>{r.title}</Link></td>
                  <td className="px-3 py-2 font-mono text-xs">
                    <span className="font-sans text-sm">{r.skus.length}: </span>{r.skus.slice(0, 3).join(', ')}{r.skus.length > 3 ? ', ...' : ''}
                    {r.inactiveSkus > 0 && <span className="ml-1 font-sans text-text-muted">({r.inactiveSkus} inactive)</span>}
                  </td>
                  <td className="px-3 py-2">{r.brand ?? <span className="text-text-muted">None</span>}</td>
                  <td className="px-3 py-2"><Badge variant={statusVariant(r.status)}>{productStatusLabel(r.status)}</Badge></td>
                  <td className="px-3 py-2 text-right font-mono">{priceRange(r)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ))}
      {data && (
        <nav aria-label="Pages" className="flex items-center gap-3 text-sm">
          <Button type="button" variant="outline" size="sm" disabled={page <= 1 || res.loading} onClick={() => setPage(page - 1)}>Previous</Button>
          <span data-testid="page-info">Page {page} of {pages} ({data.totalCount} SKUs)</span>
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
