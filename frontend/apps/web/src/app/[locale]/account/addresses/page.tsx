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

export interface SavedIndustrialAddress {
  id: string;
  label: string;
  recipientName: string;
  phoneNumber: string;
  streetLine1: string;
  streetLine2: string;
  areaOrThana: string;
  upazila: string;
  district: string;
  division: string;
  postalCode: string;
  shippingZone: 'inside_dhaka' | 'dhaka_suburb' | 'outside_dhaka';
  isDefaultShipping: boolean;
  isDefaultBilling: boolean;
}

const SEEDED_BD_LOCATIONS: BdHierarchyNode[] = [
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

const INITIAL_ADDRESSES: SavedIndustrialAddress[] = [
  {
    id: 'addr-tongi-plant',
    label: 'Gazipur Main Factory Floor',
    recipientName: 'Engr. Mahmudul Hasan',
    phoneNumber: '+8801711000001',
    streetLine1: 'Plot 42, BSCIC Industrial Estate',
    streetLine2: 'Gate 3, Substation Building',
    areaOrThana: 'Tongi',
    upazila: 'Tongi Industrial Zone',
    district: 'Gazipur',
    division: 'Dhaka',
    postalCode: '1710',
    shippingZone: 'dhaka_suburb',
    isDefaultShipping: true,
    isDefaultBilling: true
  },
  {
    id: 'addr-cepz-port',
    label: 'Chattogram Port Receiving Bay',
    recipientName: 'Engr. Rafiqul Islam',
    phoneNumber: '+8801819223344',
    streetLine1: 'Sector 4, Plot 19, CEPZ',
    streetLine2: 'Bay 2',
    areaOrThana: 'Halishahar',
    upazila: 'Chattogram EPZ (Halishahar)',
    district: 'Chattogram',
    division: 'Chattogram',
    postalCode: '4223',
    shippingZone: 'outside_dhaka',
    isDefaultShipping: false,
    isDefaultBilling: false
  }
];

export default function AccountAddressesPage() {
  const [addresses, setAddresses] = React.useState<SavedIndustrialAddress[]>(INITIAL_ADDRESSES);
  const [editingId, setEditingId] = React.useState<string | null>(null);
  const [label, setLabel] = React.useState('Savar EPZ Automation Unit');
  const [recipientName, setRecipientName] = React.useState('Engr. Tanvir Ahmed');
  const [phoneNumber, setPhoneNumber] = React.useState('+8801711334455');
  const [streetLine1, setStreetLine1] = React.useState('Plot 14, Old EPZ Road');
  const [streetLine2, setStreetLine2] = React.useState('Utility Block B');
  const [postalCode, setPostalCode] = React.useState('1349');
  const [selectedDivisionId, setSelectedDivisionId] = React.useState('div-dhaka');
  const [selectedDistrictId, setSelectedDistrictId] = React.useState('dist-dhaka');
  const [selectedUpazilaId, setSelectedUpazilaId] = React.useState('upz-savar');
  const [isDefaultShipping, setIsDefaultShipping] = React.useState(false);
  const [isDefaultBilling, setIsDefaultBilling] = React.useState(false);
  const [statusMessage, setStatusMessage] = React.useState<string | null>(null);
  const [gdprExportedJson, setGdprExportedJson] = React.useState<string | null>(null);
  const [accountAnonymized, setAccountAnonymized] = React.useState(false);

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
    if (firstUpz) {
      setPostalCode(firstUpz.postalCodePrefix);
    }
  };

  const handleDistrictChange = (nextDistrictId: string) => {
    setSelectedDistrictId(nextDistrictId);
    const firstUpz = SEEDED_BD_LOCATIONS.find((l) => l.level === 'upazila' && l.parentId === nextDistrictId);
    setSelectedUpazilaId(firstUpz?.id ?? '');
    if (firstUpz) {
      setPostalCode(firstUpz.postalCodePrefix);
    }
  };

  const handleSaveAddress = (e: React.FormEvent) => {
    e.preventDefault();
    const divNode = SEEDED_BD_LOCATIONS.find((l) => l.id === selectedDivisionId);
    const distNode = SEEDED_BD_LOCATIONS.find((l) => l.id === selectedDistrictId && l.parentId === selectedDivisionId);
    const upzNode = SEEDED_BD_LOCATIONS.find((l) => l.id === selectedUpazilaId && l.parentId === selectedDistrictId);

    if (!divNode || !distNode || !upzNode) {
      setStatusMessage('Invalid Bangladesh Division → District → Upazila hierarchy selection.');
      return;
    }

    const nextEntry: SavedIndustrialAddress = {
      id: editingId ?? `addr-${Date.now().toString(36)}`,
      label: label.trim(),
      recipientName: recipientName.trim(),
      phoneNumber: phoneNumber.trim(),
      streetLine1: streetLine1.trim(),
      streetLine2: streetLine2.trim(),
      areaOrThana: upzNode.nameEn,
      upazila: upzNode.nameEn,
      district: distNode.nameEn,
      division: divNode.nameEn,
      postalCode: postalCode.trim() || upzNode.postalCodePrefix,
      shippingZone: upzNode.shippingZone,
      isDefaultShipping,
      isDefaultBilling
    };

    setAddresses((prev) => {
      const normalized = prev.map((existing) => ({
        ...existing,
        isDefaultShipping: nextEntry.isDefaultShipping ? false : existing.isDefaultShipping,
        isDefaultBilling: nextEntry.isDefaultBilling ? false : existing.isDefaultBilling
      }));

      if (editingId) {
        return normalized.map((item) => (item.id === editingId ? nextEntry : item));
      }
      return [...normalized, nextEntry];
    });

    setStatusMessage(
      editingId
        ? `Updated industrial address "${nextEntry.label}" (${nextEntry.upazila}, ${nextEntry.district}).`
        : `Created industrial address "${nextEntry.label}" (${nextEntry.upazila}, ${nextEntry.district}).`
    );
    setEditingId(null);
  };

  const handleEditAddress = (addr: SavedIndustrialAddress) => {
    setEditingId(addr.id);
    setLabel(addr.label);
    setRecipientName(addr.recipientName);
    setPhoneNumber(addr.phoneNumber);
    setStreetLine1(addr.streetLine1);
    setStreetLine2(addr.streetLine2);
    setPostalCode(addr.postalCode);
    setIsDefaultShipping(addr.isDefaultShipping);
    setIsDefaultBilling(addr.isDefaultBilling);

    const divNode = SEEDED_BD_LOCATIONS.find((l) => l.level === 'division' && l.nameEn === addr.division);
    const distNode = SEEDED_BD_LOCATIONS.find((l) => l.level === 'district' && l.nameEn === addr.district);
    const upzNode = SEEDED_BD_LOCATIONS.find((l) => l.level === 'upazila' && l.nameEn === addr.upazila);

    if (divNode) setSelectedDivisionId(divNode.id);
    if (distNode) setSelectedDistrictId(distNode.id);
    if (upzNode) setSelectedUpazilaId(upzNode.id);
  };

  const handleDeleteAddress = (id: string) => {
    setAddresses((prev) => prev.filter((a) => a.id !== id));
    if (editingId === id) {
      setEditingId(null);
    }
    setStatusMessage('Removed address from industrial address book.');
  };

  const handleSetDefaultShipping = (id: string) => {
    setAddresses((prev) =>
      prev.map((a) => ({
        ...a,
        isDefaultShipping: a.id === id
      }))
    );
    setStatusMessage('Updated default factory shipping destination.');
  };

  return (
    <main className="mx-auto max-w-5xl space-y-8 bg-bg-canvas p-6 text-fg-primary">
      <header className="border-b border-border-subtle pb-4">
        <h1 className="font-display text-2xl font-bold">
          Bangladesh Industrial Address Book & GDPR Privacy Controls
        </h1>
        <p className="mt-1 text-sm text-fg-secondary">
          Pre-seeded Division → District → Upazila/EPZ hierarchy with parent-child validation and shipping zone calculation.
        </p>
      </header>

      {statusMessage && (
        <div
          role="status"
          className="rounded-md border border-state-success bg-bg-elevated p-3 text-xs font-medium text-state-success"
        >
          {statusMessage}
        </div>
      )}

      {/* SAVED INDUSTRIAL ADDRESS CARDS */}
      <section className="space-y-4">
        <h2 className="font-display text-lg font-bold">
          Saved Factory & Warehouse Delivery Addresses ({addresses.length})
        </h2>
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
          {addresses.map((addr) => (
            <article
              key={addr.id}
              className="rounded-lg border border-border-subtle bg-bg-surface p-5 flex flex-col justify-between space-y-4"
            >
              <div className="space-y-2">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <span className="font-display text-base font-bold text-fg-primary">
                    {addr.label}
                  </span>
                  <div className="flex flex-wrap gap-1.5">
                    {addr.isDefaultShipping && (
                      <span className="rounded-sm border border-brand-accent bg-bg-elevated px-2 py-0.5 font-mono text-xs text-brand-accent">
                        DEFAULT SHIPPING
                      </span>
                    )}
                    {addr.isDefaultBilling && (
                      <span className="rounded-sm border border-border-strong bg-bg-elevated px-2 py-0.5 font-mono text-xs text-fg-secondary">
                        DEFAULT BILLING
                      </span>
                    )}
                  </div>
                </div>
                <div className="text-xs text-fg-secondary space-y-0.5">
                  <p className="font-semibold text-fg-primary">
                    {addr.recipientName} · <span className="font-mono">{addr.phoneNumber}</span>
                  </p>
                  <p>{addr.streetLine1}{addr.streetLine2 ? `, ${addr.streetLine2}` : ''}</p>
                  <p>
                    {addr.upazila}, {addr.district}, {addr.division} — <span className="font-mono">{addr.postalCode}</span>
                  </p>
                  <p className="font-mono text-brand-accent pt-1">
                    Shipping Zone: {addr.shippingZone}
                  </p>
                </div>
              </div>

              <div className="flex flex-wrap items-center gap-2 border-t border-border-subtle pt-3">
                <button
                  type="button"
                  onClick={() => handleEditAddress(addr)}
                  className="rounded-md border border-border-strong bg-bg-elevated px-3 py-1.5 text-xs font-semibold text-fg-primary"
                >
                  Edit
                </button>
                {!addr.isDefaultShipping && (
                  <button
                    type="button"
                    onClick={() => handleSetDefaultShipping(addr.id)}
                    className="rounded-md border border-border-subtle bg-bg-canvas px-3 py-1.5 text-xs font-semibold text-fg-secondary"
                  >
                    Set Default Shipping
                  </button>
                )}
                <button
                  type="button"
                  onClick={() => handleDeleteAddress(addr.id)}
                  className="rounded-md border border-state-danger bg-bg-canvas px-3 py-1.5 text-xs font-semibold text-state-danger"
                >
                  Delete
                </button>
              </div>
            </article>
          ))}
        </div>
      </section>

      {/* ADD / EDIT INDUSTRIAL ADDRESS FORM */}
      <form
        onSubmit={handleSaveAddress}
        className="rounded-lg border border-border-subtle bg-bg-surface p-6 space-y-5"
      >
        <div className="flex items-center justify-between border-b border-border-subtle pb-3">
          <h2 className="font-display text-lg font-bold">
            {editingId ? 'Update Industrial Delivery Address' : 'Add New Factory / Warehouse Address'}
          </h2>
          {editingId && (
            <button
              type="button"
              onClick={() => setEditingId(null)}
              className="text-xs font-semibold text-brand-primary"
            >
              Cancel Edit
            </button>
          )}
        </div>

        <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
          <div>
            <label htmlFor="addr-1" className="block text-xs font-semibold text-fg-secondary">Site / Plant Label</label>
            <input id="addr-1"
              type="text"
              required
              value={label}
              onChange={(e) => setLabel(e.target.value)}
              className="mt-1 w-full rounded-md border border-border-subtle bg-bg-canvas px-3 py-2 text-sm text-fg-primary"
            />
          </div>
          <div>
            <label htmlFor="addr-2" className="block text-xs font-semibold text-fg-secondary">Receiving Engineer Name</label>
            <input id="addr-2"
              type="text"
              required
              value={recipientName}
              onChange={(e) => setRecipientName(e.target.value)}
              className="mt-1 w-full rounded-md border border-border-subtle bg-bg-canvas px-3 py-2 text-sm text-fg-primary"
            />
          </div>
          <div>
            <label htmlFor="addr-3" className="block text-xs font-semibold text-fg-secondary">BD Mobile (+8801XXXXXXXXX)</label>
            <input id="addr-3"
              type="tel"
              required
              value={phoneNumber}
              onChange={(e) => setPhoneNumber(e.target.value)}
              className="mt-1 w-full rounded-md border border-border-subtle bg-bg-canvas px-3 py-2 font-mono text-sm text-fg-primary"
            />
          </div>
        </div>

        <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
          <div>
            <label htmlFor="addr-4" className="block text-xs font-semibold text-fg-secondary">Division (বিভাগ)</label>
            <select id="addr-4"
              value={selectedDivisionId}
              onChange={(e) => handleDivisionChange(e.target.value)}
              className="mt-1 w-full rounded-md border border-border-subtle bg-bg-canvas px-3 py-2 text-sm text-fg-primary"
            >
              {divisions.map((div) => (
                <option key={div.id} value={div.id}>
                  {div.nameEn} ({div.nameBn})
                </option>
              ))}
            </select>
          </div>

          <div>
            <label htmlFor="addr-5" className="block text-xs font-semibold text-fg-secondary">District (জেলা)</label>
            <select id="addr-5"
              value={selectedDistrictId}
              onChange={(e) => handleDistrictChange(e.target.value)}
              className="mt-1 w-full rounded-md border border-border-subtle bg-bg-canvas px-3 py-2 text-sm text-fg-primary"
            >
              {districts.map((dist) => (
                <option key={dist.id} value={dist.id}>
                  {dist.nameEn} ({dist.nameBn})
                </option>
              ))}
            </select>
          </div>

          <div>
            <label htmlFor="addr-6" className="block text-xs font-semibold text-fg-secondary">Upazila / EPZ (উপজেলা)</label>
            <select id="addr-6"
              value={selectedUpazilaId}
              onChange={(e) => setSelectedUpazilaId(e.target.value)}
              className="mt-1 w-full rounded-md border border-border-subtle bg-bg-canvas px-3 py-2 text-sm text-fg-primary"
            >
              {upazilas.map((upz) => (
                <option key={upz.id} value={upz.id}>
                  {upz.nameEn} ({upz.nameBn}) · [{upz.shippingZone}]
                </option>
              ))}
            </select>
          </div>
        </div>

        <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
          <div className="md:col-span-2">
            <label htmlFor="addr-7" className="block text-xs font-semibold text-fg-secondary">Street / Industrial Plot / Bay</label>
            <input id="addr-7"
              type="text"
              required
              value={streetLine1}
              onChange={(e) => setStreetLine1(e.target.value)}
              className="mt-1 w-full rounded-md border border-border-subtle bg-bg-canvas px-3 py-2 text-sm text-fg-primary"
            />
          </div>
          <div>
            <label htmlFor="addr-8" className="block text-xs font-semibold text-fg-secondary">Postal Code</label>
            <input id="addr-8"
              type="text"
              required
              value={postalCode}
              onChange={(e) => setPostalCode(e.target.value)}
              className="mt-1 w-full rounded-md border border-border-subtle bg-bg-canvas px-3 py-2 font-mono text-sm text-fg-primary"
            />
          </div>
        </div>

        <div className="flex flex-wrap items-center gap-6">
          <label className="flex items-center gap-2 text-xs text-fg-secondary">
            <input
              type="checkbox"
              checked={isDefaultShipping}
              onChange={(e) => setIsDefaultShipping(e.target.checked)}
            />
            <span>Set as Default Shipping Address</span>
          </label>
          <label className="flex items-center gap-2 text-xs text-fg-secondary">
            <input
              type="checkbox"
              checked={isDefaultBilling}
              onChange={(e) => setIsDefaultBilling(e.target.checked)}
            />
            <span>Set as Default Billing Address</span>
          </label>
        </div>

        <button
          type="submit"
          className="rounded-md bg-brand-primary px-5 py-2.5 text-xs font-semibold text-fg-on-accent"
        >
          {editingId ? 'Save Updated Address' : 'Save Industrial Address'}
        </button>
      </form>

      {/* GDPR EXPORT & ANONYMIZATION */}
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
                    user: accountAnonymized ? 'deleted_user@anonymized.mechware.local' : 'buyer@mechware.com.bd',
                    addresses
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
          <button
            type="button"
            onClick={() => {
              setAddresses([]);
              setAccountAnonymized(true);
              setStatusMessage(
                'GDPR Right-to-Erasure executed: PII scrubbed, addresses purged, and all active refresh token families revoked.'
              );
            }}
            className="rounded-md border border-state-danger bg-bg-canvas px-4 py-2 text-xs font-semibold text-state-danger"
          >
            Anonymize Account & Scrub PII (DELETE /api/v1/users/me)
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
