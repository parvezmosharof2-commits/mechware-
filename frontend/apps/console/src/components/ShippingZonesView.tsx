'use client';

import * as React from 'react';
import Link from 'next/link';
import { Badge, Button, Skeleton } from '@mechware/ui';
import { groupByDivision, type CoverageDistrict } from '@mechware/api-client';
import { shippingError, shippingZonesApi } from '../lib/api';
import { useAuth } from '../lib/auth';
import { useResource } from '../lib/useResource';
import { ConsoleShell } from './ConsoleShell';

export function NoAccess({ permission }: { permission: string }) {
  return (
    <p role="alert" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">
      Your role does not allow this action ({permission}).
    </p>
  );
}

export function LoadFailure({ error, onRetry }: { error: unknown; onRetry: () => void }) {
  const f = shippingError(error);
  return (
    <div role="alert" className="flex flex-wrap items-center gap-3 rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">
      <span>{f.text}</span>
      {f.retry && <Button type="button" variant="outline" size="sm" onClick={onRetry}>Try again</Button>}
    </div>
  );
}

export function NoZoneBanner() {
  return (
    <div role="alert" data-testid="no-zone-banner" className="rounded-md border-2 border-fault bg-bg-subtle p-4 text-sm">
      <p className="font-bold text-fault">No active shipping zone: customers cannot place orders in production.</p>
      <p className="mt-1 text-text-muted">Orders are refused until at least one active zone covers the delivery district. Create a zone, add its districts and weight bands, and switch it to active.</p>
    </div>
  );
}

function DistrictGroups({ districts, label }: { districts: CoverageDistrict[]; label: string }) {
  if (districts.length === 0) return <p className="text-sm text-text-muted">None.</p>;
  return (
    <div className="space-y-3" aria-label={label}>
      {groupByDivision(districts).map((g) => (
        <div key={g.division}>
          <h3 className="text-xs font-semibold uppercase tracking-wider text-text-muted">{g.division} ({g.districts.length})</h3>
          <ul className="mt-1 flex flex-wrap gap-1.5">
            {g.districts.map((d) => (
              <li key={d.districtId} className="rounded-sm border border-surface-border bg-bg-subtle px-2 py-0.5 text-xs">
                {d.nameEn}
                {d.zoneCode && <span className="ml-1 font-mono text-text-muted">{d.zoneCode}{d.zoneActive === false ? ' (inactive)' : ''}</span>}
              </li>
            ))}
          </ul>
        </div>
      ))}
    </div>
  );
}

function Zones({ canManage }: { canManage: boolean }) {
  const zones = useResource(() => shippingZonesApi.list(), 'zones');
  const coverage = useResource(() => shippingZonesApi.coverage(), 'coverage');
  const cov = coverage.data?.data;
  const rows = zones.data?.data;

  return (
    <div className="space-y-5">
      {cov && cov.activeZoneCount === 0 && <NoZoneBanner />}
      {canManage && (
        <div>
          <Link href="/shipping/zones/new" className="inline-flex items-center rounded-md border border-surface-border px-4 py-2 text-sm font-semibold text-brand-accent hover:bg-bg-subtle">New zone</Link>
        </div>
      )}
      {zones.error ? <LoadFailure error={zones.error} onRetry={zones.reload} /> : null}
      {zones.loading && !rows && <div role="status"><span className="sr-only">Loading</span><Skeleton className="h-32 w-full" /></div>}
      {rows && (rows.length === 0 ? (
        <p role="status" data-testid="empty" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">
          No shipping zones yet. {canManage ? 'Create one with "New zone".' : 'An administrator needs to create one.'}
        </p>
      ) : (
        <div className="overflow-x-auto rounded-md border border-surface-border">
          <table className="w-full text-left text-sm" data-testid="zone-table">
            <caption className="sr-only">Shipping zones</caption>
            <thead className="bg-bg-subtle text-xs text-text-muted">
              <tr>
                <th scope="col" className="px-3 py-2">Code</th>
                <th scope="col" className="px-3 py-2">Name (English)</th>
                <th scope="col" className="px-3 py-2">Name (Bangla)</th>
                <th scope="col" className="px-3 py-2">Status</th>
                <th scope="col" className="px-3 py-2 text-right">Districts</th>
                <th scope="col" className="px-3 py-2 text-right">Bands</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((z) => (
                <tr key={z.id} className="border-t border-surface-border">
                  <td className="px-3 py-2"><Link className="font-mono font-semibold text-brand-accent underline" href={`/shipping/zones/${z.id}`}>{z.code}</Link></td>
                  <td className="px-3 py-2">{z.nameEn}</td>
                  <td className="px-3 py-2">{z.nameBn}</td>
                  <td className="px-3 py-2"><Badge variant={z.isActive ? 'success' : 'outline'} data-testid="active-badge">{z.isActive ? 'Active' : 'Inactive'}</Badge></td>
                  <td className="px-3 py-2 text-right font-mono">{z.districtCount}</td>
                  <td className="px-3 py-2 text-right font-mono">{z.bandCount}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ))}

      <section aria-label="Coverage" className="space-y-3 rounded-xl border border-surface-border bg-surface p-4" data-testid="coverage-panel">
        <h2 className="font-display text-lg font-bold">Coverage</h2>
        {coverage.error ? <LoadFailure error={coverage.error} onRetry={coverage.reload} /> : null}
        {coverage.loading && !cov && <Skeleton className="h-24 w-full" />}
        {cov && (
          <>
            <p className="text-sm text-text-muted" data-testid="coverage-summary">
              {cov.activeZoneCount} active {cov.activeZoneCount === 1 ? 'zone' : 'zones'}; {cov.covered.length} districts covered, {cov.uncovered.length} not covered (orders there are refused in production).
            </p>
            <details open={cov.uncovered.length > 0 && cov.uncovered.length <= 70}>
              <summary className="cursor-pointer text-sm font-semibold">Not covered ({cov.uncovered.length})</summary>
              <div className="mt-2" data-testid="uncovered"><DistrictGroups districts={cov.uncovered} label="Uncovered districts" /></div>
            </details>
            <details>
              <summary className="cursor-pointer text-sm font-semibold">Covered ({cov.covered.length})</summary>
              <div className="mt-2" data-testid="covered"><DistrictGroups districts={cov.covered} label="Covered districts" /></div>
            </details>
          </>
        )}
      </section>
    </div>
  );
}

export function ShippingZonesView() {
  const { user } = useAuth();
  const held = new Set((user?.permissions ?? []).map((p) => p.toLowerCase()));
  return (
    <ConsoleShell activeItemId="shipping-zones" title="Shipping zones">
      {held.has('shipping.view') ? <Zones canManage={held.has('shipping.manage')} /> : <NoAccess permission="shipping.view" />}
    </ConsoleShell>
  );
}
