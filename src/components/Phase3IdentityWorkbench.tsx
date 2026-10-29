import React, { useState, useEffect } from 'react';
import { Lock, Check, ShieldAlert, Building2, Sun, Moon, Trash2, Plus } from 'lucide-react';

export interface Phase3IdentityWorkbenchProps {
  locale: 'en' | 'bn';
  theme?: 'dark' | 'light';
  initialModalOpen?: boolean;
  onToggleLocale: () => void;
  onToggleTheme?: () => void;
}

interface TokenFamilyNode {
  generation: number;
  tokenHashPrefix: string;
  isUsed: boolean;
  isRevoked: boolean;
  usedAtSecondsAgo: number | null;
  ipAddress: string;
}

interface IndustrialAddressEntry {
  id: string;
  label: string;
  recipientName: string;
  phoneNumber: string;
  division: 'Dhaka' | 'Chattogram' | 'Rajshahi';
  district: string;
  upazila: string;
  postalCode: string;
  shippingZone: 'inside_dhaka' | 'dhaka_suburb' | 'outside_dhaka';
  isDefaultShipping: boolean;
}

const BD_HIERARCHY_MAP: Record<
  'Dhaka' | 'Chattogram' | 'Rajshahi',
  Record<string, { upazilas: { name: string; zone: 'inside_dhaka' | 'dhaka_suburb' | 'outside_dhaka'; postal: string }[] }>
> = {
  Dhaka: {
    Gazipur: {
      upazilas: [{ name: 'Tongi Industrial Zone', zone: 'dhaka_suburb', postal: '1710' }],
    },
    Dhaka: {
      upazilas: [
        { name: 'Tejgaon Industrial Area', zone: 'inside_dhaka', postal: '1208' },
        { name: 'Savar EPZ', zone: 'dhaka_suburb', postal: '1349' },
      ],
    },
    Narayanganj: {
      upazilas: [{ name: 'Siddhirganj Adamjee EPZ', zone: 'dhaka_suburb', postal: '1431' }],
    },
  },
  Chattogram: {
    Chattogram: {
      upazilas: [{ name: 'Chattogram EPZ (Halishahar)', zone: 'outside_dhaka', postal: '4223' }],
    },
  },
  Rajshahi: {
    Pabna: {
      upazilas: [{ name: 'Ishwardi EPZ', zone: 'outside_dhaka', postal: '6620' }],
    },
  },
};

export const Phase3IdentityWorkbench: React.FC<Phase3IdentityWorkbenchProps> = ({
  locale,
  theme = 'dark',
  initialModalOpen = false,
  onToggleLocale,
  onToggleTheme,
}) => {
  const isLight = theme === 'light';

  // Theme-adaptive semantic classes matching docs/02_DESIGN_SYSTEM.md
  const surfaceBg = isLight ? 'bg-[#FFFFFF] border-[#E2E8F0]' : 'bg-[#0A1128] border-[#1B2B55]';
  const elevatedBg = isLight ? 'bg-[#F1F5F9] border-[#CBD5E1]' : 'bg-[#101B3B] border-[#2A4078]';
  const codeWellBg = isLight ? 'bg-[#F8FAFC] border-[#CBD5E1]' : 'bg-[#060B18] border-[#1B2B55]';
  const primaryText = isLight ? 'text-[#0B132B]' : 'text-[#F8FAFC]';
  const secondaryText = isLight ? 'text-[#475569]' : 'text-[#94A3B8]';
  const mutedText = isLight ? 'text-[#64748B]' : 'text-[#64748B]';
  const accentText = isLight ? 'text-[#0F3E7D]' : 'text-[#00E5FF]';
  const primaryBtn = isLight
    ? 'bg-[#0F3E7D] text-[#FFFFFF] hover:bg-[#1652A3]'
    : 'bg-[#00E5FF] text-[#060B18] hover:bg-[#38EFFF]';

  // 1. In-Memory Token Vault & Refresh Token Family Simulator
  const [inMemoryAccessToken, setInMemoryAccessToken] = useState<string | null>(
    'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJidXllci0wMSIsInJvbGVzIjpbImJ1eWVyIl0sImV4cCI6OTAwfQ.sig_mem_only'
  );
  const [familyRevokedReason, setFamilyRevokedReason] = useState<string | null>(null);
  const [tokenChain, setTokenChain] = useState<TokenFamilyNode[]>([
    {
      generation: 1,
      tokenHashPrefix: 'sha256:8f41a9c0',
      isUsed: true,
      isRevoked: false,
      usedAtSecondsAgo: 4,
      ipAddress: '203.112.218.10',
    },
    {
      generation: 2,
      tokenHashPrefix: 'sha256:e91b74d2',
      isUsed: false,
      isRevoked: false,
      usedAtSecondsAgo: null,
      ipAddress: '203.112.218.10',
    },
  ]);

  // 2. Seller Resource Isolation Simulator (GET & PUT)
  const [authenticatedSeller, setAuthenticatedSeller] = useState<'seller-alpha' | 'seller-beta' | 'admin'>('seller-alpha');
  const [targetSellerResource, setTargetSellerResource] = useState<'seller-alpha' | 'seller-beta'>('seller-beta');
  const [httpMethod, setHttpMethod] = useState<'GET' | 'PUT'>('PUT');

  // 3. Staff Mandatory 2FA TOTP & Replay Guard (RFC 6238 §5.2)
  const [staffRole, setStaffRole] = useState<'admin' | 'finance' | 'moderator' | 'support' | 'seller'>('admin');
  const [totpCodeInput, setTotpCodeInput] = useState('492817');
  const [lastUsedTotpTimeStep, setLastUsedTotpTimeStep] = useState<number | null>(59824101);
  const [totpStatus, setTotpStatus] = useState<'verified' | 'replayed' | 'invalid'>('verified');

  // 4. Full Bangladesh Industrial Address Book CRUD
  const [addresses, setAddresses] = useState<IndustrialAddressEntry[]>([
    {
      id: 'addr-01',
      label: 'Gazipur Main Factory Floor',
      recipientName: 'Engr. Mahmudul Hasan',
      phoneNumber: '+8801711000001',
      division: 'Dhaka',
      district: 'Gazipur',
      upazila: 'Tongi Industrial Zone',
      postalCode: '1710',
      shippingZone: 'dhaka_suburb',
      isDefaultShipping: true,
    },
    {
      id: 'addr-02',
      label: 'Chattogram Port Receiving Bay',
      recipientName: 'Engr. Rafiqul Islam',
      phoneNumber: '+8801819223344',
      division: 'Chattogram',
      district: 'Chattogram',
      upazila: 'Chattogram EPZ (Halishahar)',
      postalCode: '4223',
      shippingZone: 'outside_dhaka',
      isDefaultShipping: false,
    },
  ]);
  const [newLabel, setNewLabel] = useState('Savar EPZ Automation Unit');
  const [newRecipient, setNewRecipient] = useState('Engr. Tanvir Ahmed');
  const [newPhone, setNewPhone] = useState('+8801711334455');
  const [division, setDivision] = useState<'Dhaka' | 'Chattogram' | 'Rajshahi'>('Dhaka');
  const [district, setDistrict] = useState('Gazipur');
  const [upazila, setUpazila] = useState('Tongi Industrial Zone');

  // 5. Auth Modal Preview Toggle
  const [authModalOpen, setAuthModalOpen] = useState(initialModalOpen);
  const [authModalStep, setAuthModalStep] = useState<'signin' | 'signup' | 'otp' | 'forgot'>('signin');

  useEffect(() => {
    setAuthModalOpen(initialModalOpen);
  }, [initialModalOpen]);

  const handleRotateActiveRefreshToken = () => {
    setFamilyRevokedReason(null);
    const nextGen = tokenChain.length + 1;
    const nextHash = `sha256:${Math.random().toString(16).slice(2, 10)}`;
    setTokenChain((prev) =>
      prev
        .map((node) =>
          !node.isUsed && !node.isRevoked
            ? { ...node, isUsed: true, usedAtSecondsAgo: 1 }
            : node
        )
        .concat({
          generation: nextGen,
          tokenHashPrefix: nextHash,
          isUsed: false,
          isRevoked: false,
          usedAtSecondsAgo: null,
          ipAddress: '203.112.218.10',
        })
    );
    setInMemoryAccessToken(
      `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.gen_${nextGen}_${Date.now().toString(36)}.sig_mem_only`
    );
  };

  const handleSimulateReplayAttackOutsideGrace = () => {
    setTokenChain((prev) => prev.map((node) => ({ ...node, isRevoked: true })));
    setInMemoryAccessToken(null);
    setFamilyRevokedReason(
      'BREACH DETECTED (ADR-0006): Gen #1 token replayed outside 10s grace window from IP 185.199.108.99. Entire RefreshToken FamilyId revoked & logged to audit_logs!'
    );
  };

  const handleSingleSessionCookieLogout = () => {
    setTokenChain((prev) => prev.map((node) => ({ ...node, isRevoked: true })));
    setInMemoryAccessToken(null);
    setFamilyRevokedReason(
      'Cookie-Bound Single-Session Logout: Current refresh_token FamilyId revoked without terminating other active devices.'
    );
  };

  const handleResetTokenFamily = () => {
    setFamilyRevokedReason(null);
    setInMemoryAccessToken(
      'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.fresh_family_session.sig_mem_only'
    );
    setTokenChain([
      {
        generation: 1,
        tokenHashPrefix: 'sha256:92c041b8',
        isUsed: false,
        isRevoked: false,
        usedAtSecondsAgo: null,
        ipAddress: '203.112.218.10',
      },
    ]);
  };

  const handleVerifyTotpWithReplayGuard = (simulateReplaySameStep: boolean) => {
    if (!/^\d{6}$/.test(totpCodeInput)) {
      setTotpStatus('invalid');
      return;
    }
    if (simulateReplaySameStep) {
      setTotpStatus('replayed');
      return;
    }
    setLastUsedTotpTimeStep((prev) => (prev ?? 59824101) + 1);
    setTotpStatus('verified');
  };

  const handleAddAddress = (e: React.FormEvent) => {
    e.preventDefault();
    const distEntry = BD_HIERARCHY_MAP[division]?.[district];
    const upzEntry = distEntry?.upazilas.find((u) => u.name === upazila) ?? distEntry?.upazilas[0];
    if (!upzEntry) return;

    const created: IndustrialAddressEntry = {
      id: `addr-${Date.now().toString(36)}`,
      label: newLabel.trim() || 'Industrial Plant Site',
      recipientName: newRecipient.trim() || 'Engr. Procurement',
      phoneNumber: newPhone.trim() || '+8801711000000',
      division,
      district,
      upazila: upzEntry.name,
      postalCode: upzEntry.postal,
      shippingZone: upzEntry.zone,
      isDefaultShipping: addresses.length === 0,
    };
    setAddresses((prev) => [...prev, created]);
  };

  const sellerIsolationAllowed =
    authenticatedSeller === 'admin' || authenticatedSeller === targetSellerResource;

  const rolePermissions: Record<typeof staffRole, string[]> = {
    admin: [
      'orders.view_own',
      'orders.view_all',
      'orders.refund',
      'catalog.create',
      'catalog.moderate',
      'sellers.kyc_review',
      'users.manage',
      'finance.payouts',
      'audit.view',
    ],
    finance: ['orders.view_all', 'orders.refund', 'finance.payouts', 'audit.view'],
    moderator: ['catalog.moderate', 'sellers.kyc_review'],
    support: ['orders.view_own', 'orders.view_all', 'orders.refund'],
    seller: ['orders.view_own', 'catalog.create', 'catalog.update_own', 'sellers.manage_own'],
  };

  const availableDistricts = Object.keys(BD_HIERARCHY_MAP[division]);
  const availableUpazilas = BD_HIERARCHY_MAP[division]?.[district]?.upazilas ?? [];

  return (
    <section className="max-w-[1440px] mx-auto px-4 sm:px-6 py-8 sm:py-10 space-y-8">
      <div className={`flex flex-col md:flex-row md:items-center justify-between gap-4 border-b pb-6 ${isLight ? 'border-[#CBD5E1]' : 'border-[#1B2B55]'}`}>
        <div>
          <div className={`text-xs font-mono uppercase tracking-wider font-semibold ${accentText}`}>
            {locale === 'en'
              ? 'MECHWARE IDENTITY, ACCESS & TENANT SECURITY'
              : 'মেকওয়্যার আইডেন্টিটি, অ্যাক্সেস ও টেন্যান্ট নিরাপত্তা'}
          </div>
          <h1 className={`text-2xl sm:text-3xl font-extrabold tracking-tight mt-1 ${primaryText}`}>
            {locale === 'en'
              ? 'Identity, Rotating Token Families, RBAC & Seller Isolation'
              : 'আইডেন্টিটি, টোকেন রোটেশন, আরবিএসি এবং সেলার আইসোলেশন'}
          </h1>
          <p className={`text-sm mt-1 ${secondaryText}`}>
            {locale === 'en'
              ? 'Enterprise authentication with 15-minute in-memory access tokens, httpOnly refresh token family tracking, RFC 6238 TOTP replay protection, and Bangladesh industrial address book.'
              : '১৫-মিনিটের ইন-মেমরি অ্যাক্সেস টোকেন, httpOnly রিফ্রেশ টোকেন ফ্যামিলি, TOTP রিপ্লে সুরক্ষা এবং বাংলাদেশ শিল্পাঞ্চল অ্যাড্রেস বুক।'}
          </p>
        </div>

        <div className="flex flex-wrap items-center gap-2.5">
          {onToggleTheme && (
            <button
              onClick={onToggleTheme}
              aria-label="Toggle Light and Dark Theme"
              className={`px-3.5 py-2 rounded-lg border text-xs font-semibold flex items-center gap-1.5 cursor-pointer ${elevatedBg} ${primaryText}`}
            >
              {isLight ? <Moon className="w-3.5 h-3.5" /> : <Sun className="w-3.5 h-3.5" />}
              <span>{isLight ? 'Dark Theme' : 'Light Theme'}</span>
            </button>
          )}
          <button
            onClick={onToggleLocale}
            className={`px-3.5 py-2 rounded-lg border text-xs font-semibold cursor-pointer ${elevatedBg} ${primaryText}`}
          >
            {locale === 'en' ? 'বাংলা ভাষায় দেখুন (BN)' : 'Switch to English (EN)'}
          </button>
          <button
            onClick={() => setAuthModalOpen(true)}
            className={`px-4 py-2 rounded-lg text-xs font-bold cursor-pointer ${primaryBtn}`}
          >
            {locale === 'en' ? 'Open Bilingual Auth Modal' : 'অথেনটিকেশন মডাল খুলুন'}
          </button>
        </div>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {/* CARD 1: IN-MEMORY ACCESS JWT & REFRESH TOKEN FAMILY */}
        <div className={`rounded-xl border p-5 sm:p-6 space-y-4 ${surfaceBg}`}>
          <div className="flex flex-wrap items-center justify-between gap-2">
            <span className={`text-xs font-mono font-bold ${accentText}`}>
              ZERO-STORAGE TOKEN VAULT · ADR-0006
            </span>
            <span className="text-xs font-mono text-[#16A34A] dark:text-[#34D399] font-semibold">
              localStorage: 0 · sessionStorage: 0
            </span>
          </div>
          <h2 className={`text-lg font-bold ${primaryText}`}>
            {locale === 'en'
              ? '15m In-Memory Access JWT + Rotating httpOnly Refresh Token Family'
              : '১৫ মিনিট ইন-মেমরি অ্যাক্সেস টোকেন ও রোটেটিং রিফ্রেশ টোকেন ফ্যামিলি'}
          </h2>
          <p className={`text-xs leading-relaxed ${secondaryText}`}>
            {locale === 'en'
              ? 'No token is ever readable by JavaScript except the short-lived 15-minute access token in closure memory. Replaying a rotated refresh token outside the 10-second same-IP grace window revokes the entire token family.'
              : 'জাভাস্ক্রিপ্ট শুধুমাত্র মেমরিতে থাকা ১৫ মিনিটের অ্যাক্সেস টোকেন পড়তে পারে। ব্যবহৃত রিফ্রেশ টোকেন পুনরায় ব্যবহারের চেষ্টা করলে সম্পূর্ণ টোকেন ফ্যামিলি বাতিল হয়।'}
          </p>

          <div className={`rounded-lg border p-3 font-mono text-xs space-y-1 ${codeWellBg}`}>
            <div className={secondaryText}>InMemoryTokenVault.getToken():</div>
            <div className={`truncate font-semibold ${accentText}`}>
              {inMemoryAccessToken ?? 'null (Session Revoked)'}
            </div>
            <div className={`${mutedText} pt-1 break-all`}>
              Set-Cookie: refresh_token=[REDACTED]; Path=/api/v1/auth; HttpOnly; Secure; SameSite=Lax
            </div>
          </div>

          {familyRevokedReason && (
            <div className={`rounded-lg border border-[#DC2626] p-3 text-xs text-[#DC2626] dark:text-[#F87171] flex items-start gap-2 ${elevatedBg}`}>
              <ShieldAlert className="w-4 h-4 shrink-0 mt-0.5" />
              <span>{familyRevokedReason}</span>
            </div>
          )}

          <div className="space-y-2">
            {tokenChain.map((node) => (
              <div
                key={node.generation}
                className={`flex flex-wrap items-center justify-between gap-2 rounded-lg border px-3 py-2 text-xs font-mono ${elevatedBg} ${primaryText}`}
              >
                <span>
                  Gen #{node.generation} · {node.tokenHashPrefix} ({node.ipAddress})
                </span>
                <span
                  className={
                    node.isRevoked
                      ? 'text-[#DC2626] dark:text-[#F87171] font-bold'
                      : node.isUsed
                      ? 'text-[#D97706] dark:text-[#FBBF24] font-semibold'
                      : 'text-[#16A34A] dark:text-[#34D399] font-bold'
                  }
                >
                  {node.isRevoked
                    ? 'FAMILY REVOKED'
                    : node.isUsed
                    ? `USED (${node.usedAtSecondsAgo}s ago · 10s Grace)`
                    : 'ACTIVE CURRENT'}
                </span>
              </div>
            ))}
          </div>

          <div className="flex flex-wrap gap-2 pt-2">
            <button
              onClick={handleRotateActiveRefreshToken}
              className={`px-3 py-2 rounded-lg text-xs font-bold cursor-pointer ${primaryBtn}`}
            >
              Rotate Refresh Token
            </button>
            <button
              onClick={handleSimulateReplayAttackOutsideGrace}
              className={`px-3 py-2 rounded-lg border border-[#DC2626] text-[#DC2626] dark:text-[#F87171] text-xs font-semibold cursor-pointer ${elevatedBg}`}
            >
              Simulate Reuse Breach
            </button>
            <button
              onClick={handleSingleSessionCookieLogout}
              className={`px-3 py-2 rounded-lg border text-xs font-semibold cursor-pointer ${elevatedBg} ${primaryText}`}
            >
              Cookie Logout (Single Family)
            </button>
            <button
              onClick={handleResetTokenFamily}
              className={`px-3 py-2 rounded-lg border text-xs cursor-pointer ${codeWellBg} ${secondaryText}`}
            >
              Reset
            </button>
          </div>
        </div>

        {/* CARD 2: SELLER RESOURCE ISOLATION & RBAC */}
        <div className={`rounded-xl border p-5 sm:p-6 space-y-4 ${surfaceBg}`}>
          <div className="flex flex-wrap items-center justify-between gap-2">
            <span className={`text-xs font-mono font-bold ${accentText}`}>
              TENANT IDOR PREVENTION · READ & MODIFY GUARD
            </span>
            <span className="text-xs font-mono text-[#16A34A] dark:text-[#34D399] font-semibold">
              SellerResourceIsolationHandler
            </span>
          </div>
          <h2 className={`text-lg font-bold ${primaryText}`}>
            {locale === 'en'
              ? 'Cross-Seller Resource Isolation (GET/PUT) & [RequirePermission]'
              : 'ক্রস-সেলার রিসোর্স আইসোলেশন (GET/PUT) ও পারমিশন গার্ড'}
          </h2>
          <p className={`text-xs leading-relaxed ${secondaryText}`}>
            {locale === 'en'
              ? 'Ensures Seller Alpha cannot read (GET) or modify (PUT) Seller Beta’s inventory, catalog items, or sub-orders.'
              : 'সেলার আলফা অন্য কোনো সেলারের (সেলার বিটা) ইনভেন্টরি, পণ্য বা অর্ডার পড়তে বা পরিবর্তন করতে পারবে না।'}
          </p>

          <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
            <div>
              <label className={`block text-xs mb-1 font-medium ${secondaryText}`}>JWT Principal</label>
              <select
                value={authenticatedSeller}
                onChange={(e) => setAuthenticatedSeller(e.target.value as typeof authenticatedSeller)}
                aria-label="Authenticated JWT Principal"
                className={`w-full h-9 rounded-lg border px-2.5 text-xs ${elevatedBg} ${primaryText}`}
              >
                <option value="seller-alpha">Seller Alpha (Dhaka)</option>
                <option value="seller-beta">Seller Beta (Chattogram)</option>
                <option value="admin">System Admin (All)</option>
              </select>
            </div>
            <div>
              <label className={`block text-xs mb-1 font-medium ${secondaryText}`}>HTTP Operation</label>
              <select
                value={httpMethod}
                onChange={(e) => setHttpMethod(e.target.value as 'GET' | 'PUT')}
                aria-label="HTTP Operation"
                className={`w-full h-9 rounded-lg border px-2.5 text-xs font-mono ${elevatedBg} ${primaryText}`}
              >
                <option value="GET">GET (Read Resource)</option>
                <option value="PUT">PUT (Modify Price/Stock)</option>
              </select>
            </div>
            <div>
              <label className={`block text-xs mb-1 font-medium ${secondaryText}`}>Target Seller Resource</label>
              <select
                value={targetSellerResource}
                onChange={(e) => setTargetSellerResource(e.target.value as typeof targetSellerResource)}
                aria-label="Target Seller Resource"
                className={`w-full h-9 rounded-lg border px-2.5 text-xs ${elevatedBg} ${primaryText}`}
              >
                <option value="seller-alpha">Seller Alpha Resource</option>
                <option value="seller-beta">Seller Beta Resource</option>
              </select>
            </div>
          </div>

          <div
            className={`rounded-lg border p-4 text-xs font-mono ${codeWellBg} ${
              sellerIsolationAllowed
                ? 'border-[#16A34A] text-[#16A34A] dark:border-[#34D399] dark:text-[#34D399]'
                : 'border-[#DC2626] text-[#DC2626] dark:border-[#F87171] dark:text-[#F87171]'
            }`}
          >
            {sellerIsolationAllowed ? (
              <div className="flex items-center gap-2">
                <Check className="w-4 h-4 shrink-0" />
                <span>
                  HTTP 200 OK ({httpMethod}) — SellerResourceIsolationHandler Succeeded (seller_id matches owner)
                </span>
              </div>
            ) : (
              <div className="flex items-center gap-2">
                <Lock className="w-4 h-4 shrink-0" />
                <span>
                  HTTP 403 FORBIDDEN ({httpMethod}) — SELLER_ISOLATION_FORBIDDEN: Seller cannot read or modify another seller&apos;s resources.
                </span>
              </div>
            )}
          </div>

          <div className={`border-t pt-4 space-y-2 ${isLight ? 'border-[#CBD5E1]' : 'border-[#1B2B55]'}`}>
            <div className="flex flex-wrap items-center justify-between gap-2">
              <span className={`text-xs font-semibold ${primaryText}`}>
                Console Role-Aware Sidebar & Mandatory 2FA Check
              </span>
              <select
                value={staffRole}
                onChange={(e) => setStaffRole(e.target.value as typeof staffRole)}
                aria-label="Staff Role Selector"
                className={`rounded border px-2.5 py-1 text-xs font-mono ${elevatedBg} ${accentText}`}
              >
                <option value="admin">Role: admin (2FA Mandatory)</option>
                <option value="finance">Role: finance (2FA Mandatory)</option>
                <option value="moderator">Role: moderator</option>
                <option value="support">Role: support</option>
                <option value="seller">Role: seller</option>
              </select>
            </div>

            <div className="flex flex-wrap gap-1.5">
              {rolePermissions[staffRole].map((perm) => (
                <span
                  key={perm}
                  className={`rounded border px-2 py-0.5 font-mono text-[11px] ${elevatedBg} ${primaryText}`}
                >
                  [RequirePermission(&quot;{perm}&quot;)]
                </span>
              ))}
            </div>
          </div>
        </div>
      </div>

      {/* CARD 3 & 4: BANGLADESH ADDRESS BOOK CRUD + RFC 6238 TOTP REPLAY GUARD */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        <div className={`rounded-xl border p-5 sm:p-6 space-y-4 ${surfaceBg}`}>
          <div className={`flex items-center gap-2 text-xs font-mono font-bold ${accentText}`}>
            <Building2 className="w-4 h-4 shrink-0" />
            <span>BANGLADESH ADDRESS BOOK CRUD · 3-LEVEL HIERARCHY</span>
          </div>
          <h2 className={`text-lg font-bold ${primaryText}`}>
            {locale === 'en'
              ? 'Industrial Address Book (Division → District → Upazila)'
              : 'বাংলাদেশ শিল্পাঞ্চল অ্যাড্রেস বুক (বিভাগ → জেলা → উপজেলা)'}
          </h2>

          <form onSubmit={handleAddAddress} className="space-y-3">
            <div className="grid grid-cols-1 sm:grid-cols-3 gap-2.5">
              <div>
                <label className={`block text-xs mb-1 ${secondaryText}`}>
                  {locale === 'en' ? 'Plant Label' : 'প্ল্যান্ট নাম'}
                </label>
                <input
                  type="text"
                  value={newLabel}
                  onChange={(e) => setNewLabel(e.target.value)}
                  className={`w-full h-9 rounded-lg border px-2.5 text-xs ${elevatedBg} ${primaryText}`}
                />
              </div>
              <div>
                <label className={`block text-xs mb-1 ${secondaryText}`}>
                  {locale === 'en' ? 'Recipient' : 'প্রাপক'}
                </label>
                <input
                  type="text"
                  value={newRecipient}
                  onChange={(e) => setNewRecipient(e.target.value)}
                  className={`w-full h-9 rounded-lg border px-2.5 text-xs ${elevatedBg} ${primaryText}`}
                />
              </div>
              <div>
                <label className={`block text-xs mb-1 ${secondaryText}`}>
                  {locale === 'en' ? 'BD Phone' : 'মোবাইল নম্বর'}
                </label>
                <input
                  type="tel"
                  value={newPhone}
                  onChange={(e) => setNewPhone(e.target.value)}
                  className={`w-full h-9 rounded-lg border px-2.5 font-mono text-xs ${elevatedBg} ${primaryText}`}
                />
              </div>
            </div>

            <div className="grid grid-cols-1 sm:grid-cols-3 gap-2.5">
              <div>
                <label className={`block text-xs mb-1 ${secondaryText}`}>
                  {locale === 'en' ? 'Division' : 'বিভাগ'}
                </label>
                <select
                  value={division}
                  onChange={(e) => {
                    const nextDiv = e.target.value as typeof division;
                    setDivision(nextDiv);
                    const firstDist = Object.keys(BD_HIERARCHY_MAP[nextDiv])[0];
                    setDistrict(firstDist);
                    const firstUpz = BD_HIERARCHY_MAP[nextDiv][firstDist].upazilas[0].name;
                    setUpazila(firstUpz);
                  }}
                  aria-label="Division"
                  className={`w-full h-9 rounded-lg border px-2.5 text-xs ${elevatedBg} ${primaryText}`}
                >
                  <option value="Dhaka">Dhaka (ঢাকা)</option>
                  <option value="Chattogram">Chattogram (চট্টগ্রাম)</option>
                  <option value="Rajshahi">Rajshahi (রাজশাহী)</option>
                </select>
              </div>
              <div>
                <label className={`block text-xs mb-1 ${secondaryText}`}>
                  {locale === 'en' ? 'District' : 'জেলা'}
                </label>
                <select
                  value={district}
                  onChange={(e) => {
                    const nextDist = e.target.value;
                    setDistrict(nextDist);
                    const firstUpz = BD_HIERARCHY_MAP[division][nextDist]?.upazilas[0]?.name ?? '';
                    setUpazila(firstUpz);
                  }}
                  aria-label="District"
                  className={`w-full h-9 rounded-lg border px-2.5 text-xs ${elevatedBg} ${primaryText}`}
                >
                  {availableDistricts.map((d) => (
                    <option key={d} value={d}>
                      {d}
                    </option>
                  ))}
                </select>
              </div>
              <div>
                <label className={`block text-xs mb-1 ${secondaryText}`}>
                  {locale === 'en' ? 'Upazila / EPZ' : 'উপজেলা / ইপিজেড'}
                </label>
                <select
                  value={upazila}
                  onChange={(e) => setUpazila(e.target.value)}
                  aria-label="Upazila"
                  className={`w-full h-9 rounded-lg border px-2.5 text-xs ${elevatedBg} ${primaryText}`}
                >
                  {availableUpazilas.map((u) => (
                    <option key={u.name} value={u.name}>
                      {u.name}
                    </option>
                  ))}
                </select>
              </div>
            </div>

            <button
              type="submit"
              className={`px-3.5 py-2 rounded-lg text-xs font-bold flex items-center gap-1.5 cursor-pointer ${primaryBtn}`}
            >
              <Plus className="w-3.5 h-3.5" />
              <span>{locale === 'en' ? 'Add Verified BD Address' : 'নতুন ঠিকানা যোগ করুন'}</span>
            </button>
          </form>

          <div className="space-y-2 pt-1">
            {addresses.map((addr) => (
              <div
                key={addr.id}
                className={`flex flex-wrap items-center justify-between gap-2 rounded-lg border p-3 text-xs ${codeWellBg}`}
              >
                <div>
                  <div className={`font-bold ${primaryText}`}>
                    {addr.label}{' '}
                    {addr.isDefaultShipping && (
                      <span className={`font-mono text-[11px] ${accentText}`}>[DEFAULT]</span>
                    )}
                  </div>
                  <div className={secondaryText}>
                    {addr.recipientName} ({addr.phoneNumber}) · {addr.upazila}, {addr.district}, {addr.division} [{addr.shippingZone}]
                  </div>
                </div>
                <button
                  type="button"
                  onClick={() => setAddresses((prev) => prev.filter((item) => item.id !== addr.id))}
                  aria-label={`Delete ${addr.label}`}
                  className="p-1.5 rounded border border-[#DC2626] text-[#DC2626] dark:text-[#F87171] cursor-pointer"
                >
                  <Trash2 className="w-3.5 h-3.5" />
                </button>
              </div>
            ))}
          </div>
        </div>

        <div className={`rounded-xl border p-5 sm:p-6 space-y-4 ${surfaceBg}`}>
          <div className={`text-xs font-mono font-bold ${accentText}`}>
            RFC 6238 §5.2 TOTP REPLAY PROTECTION & EF CORE AUDIT INTERCEPTOR
          </div>
          <h2 className={`text-lg font-bold ${primaryText}`}>
            {locale === 'en'
              ? 'One-Time TOTP Step Tracking & Secret-Redacted Audit Log'
              : 'ওয়ান-টাইম TOTP স্টেপ ট্র্যাকিং এবং সিক্রেট-রিডাক্টেড অডিট লগ'}
          </h2>
          <p className={`text-xs leading-relaxed ${secondaryText}`}>
            {locale === 'en'
              ? 'Each accepted 6-digit TOTP code records its 30-second time-step in User.LastUsedTotpTimeStep. Replaying the same code within the ±1 window is immediately rejected.'
              : 'প্রতিটি গৃহীত ৬-ডিজিট TOTP কোডের টাইম-স্টেপ সংরক্ষিত থাকে। একই উইন্ডোতে কোডটি পুনরায় ব্যবহার করলে তাৎক্ষণিকভাবে প্রত্যাখ্যাত হয়।'}
          </p>

          <div className="flex flex-wrap items-center gap-2.5">
            <input
              type="text"
              value={totpCodeInput}
              onChange={(e) => setTotpCodeInput(e.target.value)}
              maxLength={6}
              aria-label="6-Digit RFC 6238 TOTP Code"
              className={`h-9 w-28 rounded-lg border px-3 font-mono text-xs ${elevatedBg} ${primaryText}`}
            />
            <button
              onClick={() => handleVerifyTotpWithReplayGuard(false)}
              className={`px-3 py-2 rounded-lg text-xs font-bold cursor-pointer ${primaryBtn}`}
            >
              Verify Fresh Step
            </button>
            <button
              onClick={() => handleVerifyTotpWithReplayGuard(true)}
              className={`px-3 py-2 rounded-lg border border-[#DC2626] text-[#DC2626] dark:text-[#F87171] text-xs font-semibold cursor-pointer ${elevatedBg}`}
            >
              Replay Same Step
            </button>
          </div>

          <div className={`rounded-lg border p-3 font-mono text-xs ${codeWellBg}`}>
            {totpStatus === 'verified' && (
              <span className="text-[#16A34A] dark:text-[#34D399] font-semibold">
                ✓ 2FA Verified — Recorded LastUsedTotpTimeStep = {lastUsedTotpTimeStep}
              </span>
            )}
            {totpStatus === 'replayed' && (
              <span className="text-[#DC2626] dark:text-[#F87171] font-semibold">
                ✗ REPLAY BLOCKED (RFC 6238 §5.2): candidateStep &lt;= LastUsedTotpTimeStep ({lastUsedTotpTimeStep})
              </span>
            )}
            {totpStatus === 'invalid' && (
              <span className="text-[#DC2626] dark:text-[#F87171] font-semibold">
                ✗ Invalid 6-digit TOTP format
              </span>
            )}
          </div>

          <pre className={`overflow-x-auto rounded-lg border p-3 font-mono text-[11px] ${codeWellBg} ${secondaryText}`}>
{`// Captured by AuditLogSaveChangesInterceptor:
{
  "action": "users.updated",
  "actorId": "0195f2e0-7c1b-7000-8000-000000000001",
  "actorIp": "203.112.218.10",
  "oldValues": { "LastUsedTotpTimeStep": ${(lastUsedTotpTimeStep ?? 59824101) - 1}, "PasswordHash": "[REDACTED]" },
  "newValues": { "LastUsedTotpTimeStep": ${lastUsedTotpTimeStep ?? 59824101}, "PasswordHash": "[REDACTED]" }
}`}
          </pre>
        </div>
      </div>

      {/* BILINGUAL AUTH MODAL (EN / BN) */}
      {authModalOpen && (
        <div
          role="dialog"
          aria-modal="true"
          aria-label="MECHWARE Authentication Portal"
          className="fixed inset-0 z-50 flex items-center justify-center bg-black/75 backdrop-blur-sm p-4"
        >
          <div className={`w-full max-w-md rounded-xl border p-6 space-y-4 ${surfaceBg}`}>
            <div className={`flex items-center justify-between border-b pb-3 ${isLight ? 'border-[#CBD5E1]' : 'border-[#1B2B55]'}`}>
              <div>
                <div className={`text-xs font-mono font-bold ${accentText}`}>
                  {locale === 'en' ? 'MECHWARE INDUSTRIAL PORTAL (EN)' : 'মেকওয়্যার ইন্ডাস্ট্রিয়াল পোর্টাল (BN)'}
                </div>
                <h3 className={`text-lg font-bold ${primaryText}`}>
                  {locale === 'en' ? 'Procurement & Engineer Sign In' : 'প্রকিউরমেন্ট ও ইঞ্জিনিয়ার সাইন ইন'}
                </h3>
              </div>
              <button
                onClick={() => setAuthModalOpen(false)}
                aria-label="Close modal"
                className={`px-2.5 py-1 rounded border text-xs cursor-pointer ${elevatedBg} ${primaryText}`}
              >
                ✕
              </button>
            </div>

            <div className="flex flex-wrap gap-2 text-xs">
              {(['signin', 'signup', 'otp', 'forgot'] as const).map((tab) => (
                <button
                  key={tab}
                  onClick={() => setAuthModalStep(tab)}
                  className={`px-2.5 py-1 rounded font-mono uppercase cursor-pointer ${
                    authModalStep === tab
                      ? primaryBtn
                      : `${elevatedBg} ${secondaryText}`
                  }`}
                >
                  {tab}
                </button>
              ))}
            </div>

            <div className="space-y-3 text-xs">
              <div>
                <label className={`block mb-1 font-semibold ${secondaryText}`}>
                  {locale === 'en' ? 'Corporate Email or BD Phone (+880)' : 'কর্পোরেট ইমেইল বা মোবাইল নম্বর (+880)'}
                </label>
                <input
                  type="text"
                  defaultValue="buyer@mechware.com.bd"
                  className={`w-full h-9 rounded-lg border px-3 ${codeWellBg} ${primaryText}`}
                />
              </div>
              {authModalStep !== 'otp' && (
                <div>
                  <label className={`block mb-1 font-semibold ${secondaryText}`}>
                    {locale === 'en' ? 'Security Password' : 'নিরাপত্তা পাসওয়ার্ড'}
                  </label>
                  <input
                    type="password"
                    defaultValue="BuyerPass#2026!"
                    className={`w-full h-9 rounded-lg border px-3 ${codeWellBg} ${primaryText}`}
                  />
                </div>
              )}
              {authModalStep === 'otp' && (
                <div>
                  <label className={`block mb-1 font-semibold ${secondaryText}`}>
                    {locale === 'en' ? '6-Digit SMS OTP (Max 3 Attempts)' : '৬-ডিজিট ওটিপি যাচাইকরণ কোড (সর্বোচ্চ ৩ বার)'}
                  </label>
                  <input
                    type="text"
                    defaultValue="654321"
                    className={`w-full h-9 rounded-lg border px-3 font-mono text-center tracking-widest ${codeWellBg} ${primaryText}`}
                  />
                </div>
              )}
              <button
                onClick={() => setAuthModalOpen(false)}
                className={`w-full h-10 rounded-lg font-bold cursor-pointer ${primaryBtn}`}
              >
                {locale === 'en' ? 'Sign In & Store 15m JWT in Memory' : 'মেকওয়্যারে সাইন ইন করুন'}
              </button>
            </div>
          </div>
        </div>
      )}
    </section>
  );
};
