'use client';

import * as React from 'react';

export interface BdHierarchyNode {
  id: string;
  parentId: string | null;
  level: 'division' | 'district' | 'upazila';
  nameEn: string;
  nameBn: string;
  shippingZone: 'inside_dhaka' | 'dhaka_suburb' | 'outside_dhaka';
  postalCodePrefix: string;
}

export const SEEDED_BD_LOCATIONS: BdHierarchyNode[] = [
  { id: 'div-dhaka', parentId: null, level: 'division', nameEn: 'Dhaka', nameBn: 'ঢাকা', shippingZone: 'inside_dhaka', postalCodePrefix: '1000' },
  { id: 'div-ctg', parentId: null, level: 'division', nameEn: 'Chattogram', nameBn: 'চট্টগ্রাম', shippingZone: 'outside_dhaka', postalCodePrefix: '4000' },
  { id: 'div-raj', parentId: null, level: 'division', nameEn: 'Rajshahi', nameBn: 'রাজশাহী', shippingZone: 'outside_dhaka', postalCodePrefix: '6000' },

  { id: 'dist-dhaka', parentId: 'div-dhaka', level: 'district', nameEn: 'Dhaka', nameBn: 'ঢাকা', shippingZone: 'inside_dhaka', postalCodePrefix: '1200' },
  { id: 'dist-gazipur', parentId: 'div-dhaka', level: 'district', nameEn: 'Gazipur', nameBn: 'গাজীপুর', shippingZone: 'dhaka_suburb', postalCodePrefix: '1700' },
  { id: 'dist-narayanganj', parentId: 'div-dhaka', level: 'district', nameEn: 'Narayanganj', nameBn: 'নারায়ণগঞ্জ', shippingZone: 'dhaka_suburb', postalCodePrefix: '1400' },
  { id: 'dist-ctg', parentId: 'div-ctg', level: 'district', nameEn: 'Chattogram', nameBn: 'চট্টগ্রাম', shippingZone: 'outside_dhaka', postalCodePrefix: '4000' },
  { id: 'dist-pabna', parentId: 'div-raj', level: 'district', nameEn: 'Pabna', nameBn: 'পাবনা', shippingZone: 'outside_dhaka', postalCodePrefix: '6600' },

  { id: 'upz-tejgaon', parentId: 'dist-dhaka', level: 'upazila', nameEn: 'Tejgaon Industrial Area', nameBn: 'তেজগাঁও শিল্পাঞ্চল', shippingZone: 'inside_dhaka', postalCodePrefix: '1208' },
  { id: 'upz-savar', parentId: 'dist-dhaka', level: 'upazila', nameEn: 'Savar EPZ', nameBn: 'সাভার ইপিজেড', shippingZone: 'dhaka_suburb', postalCodePrefix: '1349' },
  { id: 'upz-tongi', parentId: 'dist-gazipur', level: 'upazila', nameEn: 'Tongi Industrial Zone', nameBn: 'টঙ্গী শিল্পাঞ্চল', shippingZone: 'dhaka_suburb', postalCodePrefix: '1710' },
  { id: 'upz-siddhirganj', parentId: 'dist-narayanganj', level: 'upazila', nameEn: 'Siddhirganj Adamjee EPZ', nameBn: 'সিদ্ধিরগঞ্জ আদমজী ইপিজেড', shippingZone: 'dhaka_suburb', postalCodePrefix: '1431' },
  { id: 'upz-cepz', parentId: 'dist-ctg', level: 'upazila', nameEn: 'Chattogram EPZ (Halishahar)', nameBn: 'চট্টগ্রাম ইপিজেড (হালিশহর)', shippingZone: 'outside_dhaka', postalCodePrefix: '4223' },
  { id: 'upz-ishwardi', parentId: 'dist-pabna', level: 'upazila', nameEn: 'Ishwardi EPZ', nameBn: 'ঈশ্বরদী ইপিজেড', shippingZone: 'outside_dhaka', postalCodePrefix: '6620' }
];

export default function AccountAddressesPage() {
  const [selectedDivisionId, setSelectedDivisionId] = React.useState('div-dhaka');
  const [selectedDistrictId, setSelectedDistrictId] = React.useState('dist-gazipur');
  const [selectedUpazilaId, setSelectedUpazilaId] = React.useState('upz-tongi');
  const [gdprExportedJson, setGdprExportedJson] = React.useState<string | null>(null);

  const divisions = SEEDED_BD_LOCATIONS.filter((l) => l.level === 'division');
  const districts = SEEDED_BD_LOCATIONS.filter((l) => l.level === 'district' && l.parentId === selectedDivisionId);
  const upazilas = SEEDED_BD_LOCATIONS.filter((l) => l.level === 'upazila' && l.parentId === selectedDistrictId);

  const handleDivisionChange = (nextDivisionId: string) => {
    setSelectedDivisionId(nextDivisionId);
    const firstDist = SEEDED_BD_LOCATIONS.find((l) => l.level === 'district' && l.parentId === nextDivisionId);
    const nextDistId = firstDist?.id ?? '';
    setSelectedDistrictId(nextDistId);
    const firstUpz = SEEDED_BD_LOCATIONS.find((l) => l.level === 'upazila' && l.parentId === nextDistId);
    setSelectedUpazilaId(firstUpz?.id ?? '');
  };

  return (
    <main className="mx-auto max-w-5xl space-y-8 bg-bg-canvas p-6 text-fg-primary">
      <header className="border-b border-border-subtle pb-4">
        <h1 className="font-display text-2xl font-bold">
          Bangladesh Industrial Address Book & GDPR Privacy Controls
        </h1>
        <p className="mt-1 text-sm text-fg-secondary">
          Pre-seeded Division → District → Upazila/EPZ hierarchy with shipping zone calculation.
        </p>
      </header>

      <section className="grid grid-cols-1 gap-6 md:grid-cols-3">
        <div>
          <label className="block text-xs font-semibold text-fg-secondary">Division (বিভাগ)</label>
          <select
            value={selectedDivisionId}
            onChange={(e) => handleDivisionChange(e.target.value)}
            className="mt-1 w-full rounded-md border border-border-subtle bg-bg-surface px-3 py-2 text-sm text-fg-primary"
          >
            {divisions.map((div) => (
              <option key={div.id} value={div.id}>
                {div.nameEn} ({div.nameBn})
              </option>
            ))}
          </select>
        </div>

        <div>
          <label className="block text-xs font-semibold text-fg-secondary">District (জেলা)</label>
          <select
            value={selectedDistrictId}
            onChange={(e) => setSelectedDistrictId(e.target.value)}
            className="mt-1 w-full rounded-md border border-border-subtle bg-bg-surface px-3 py-2 text-sm text-fg-primary"
          >
            {districts.map((dist) => (
              <option key={dist.id} value={dist.id}>
                {dist.nameEn} ({dist.nameBn})
              </option>
            ))}
          </select>
        </div>

        <div>
          <label className="block text-xs font-semibold text-fg-secondary">Upazila / EPZ (উপজেলা)</label>
          <select
            value={selectedUpazilaId}
            onChange={(e) => setSelectedUpazilaId(e.target.value)}
            className="mt-1 w-full rounded-md border border-border-subtle bg-bg-surface px-3 py-2 text-sm text-fg-primary"
          >
            {upazilas.map((upz) => (
              <option key={upz.id} value={upz.id}>
                {upz.nameEn} ({upz.nameBn}) · [{upz.shippingZone}]
              </option>
            ))}
          </select>
        </div>
      </section>

      <section className="rounded-lg border border-border-subtle bg-bg-surface p-5 space-y-4">
        <h2 className="font-display text-lg font-bold">GDPR Right-to-Access & Right-to-Erasure</h2>
        <div className="flex flex-wrap gap-3">
          <button
            type="button"
            onClick={() =>
              setGdprExportedJson(
                JSON.stringify(
                  {
                    exportedAtUtc: new Date().toISOString(),
                    user: 'buyer@mechware.com.bd',
                    selectedDivisionId,
                    selectedDistrictId,
                    selectedUpazilaId
                  },
                  null,
                  2
                )
              )
            }
            className="rounded-md bg-brand-primary px-4 py-2 text-xs font-semibold text-fg-on-accent"
          >
            Export Personal Data (GDPR JSON)
          </button>
        </div>
        {gdprExportedJson && (
          <pre className="overflow-x-auto rounded-md border border-border-subtle bg-bg-canvas p-3 font-mono text-xs text-fg-secondary">
            {gdprExportedJson}
          </pre>
        )}
      </section>
    </main>
  );
}
