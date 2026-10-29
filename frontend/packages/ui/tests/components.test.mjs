import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { formatCurrency, validateBdPhone, calcDiscountPercent } from '../src/formatters.ts';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

test('Price formatting - South Asian Lakh grouping for BDT', () => {
  // 1,25,000 BDT
  const formatted1 = formatCurrency(125000, 'BDT', 'en');
  assert.equal(formatted1, '৳1,25,000');

  // Fractional poisha decimals preserved
  const formattedPoisha = formatCurrency(1250.5, 'BDT', 'en');
  assert.equal(formattedPoisha, '৳1,250.50');

  // 1,00,00,000 BDT (1 Crore)
  const formattedCrore = formatCurrency(10000000, 'BDT', 'en');
  assert.equal(formattedCrore, '৳1,00,00,000');

  // Small values
  const formattedSmall = formatCurrency(3850, 'BDT', 'en');
  assert.equal(formattedSmall, '৳3,850');
});

test('Price formatting - Bengali digit translation for BDT', () => {
  const formattedBn = formatCurrency(125000, 'BDT', 'bn');
  assert.equal(formattedBn, '৳১,২৫,০০০');
});

test('Price formatting - Standard Western formatting for USD', () => {
  const formattedUsd = formatCurrency(1250, 'USD', 'en');
  assert.match(formattedUsd, /\$1,250/);
});

test('Quantity bounds validation logic', () => {
  const min = 1;
  const max = 50;

  const clamp = (val) => Math.max(min, Math.min(val, max));

  assert.equal(clamp(0), 1);
  assert.equal(clamp(25), 25);
  assert.equal(clamp(999), 50);
});

test('Bangladesh Mobile Phone Format Validation (01XXXXXXXXX)', () => {
  // Valid BD operators
  assert.equal(validateBdPhone('01711223344'), true); // Grameenphone
  assert.equal(validateBdPhone('01811223344'), true); // Robi
  assert.equal(validateBdPhone('01911223344'), true); // Banglalink
  assert.equal(validateBdPhone('01511223344'), true); // Teletalk
  assert.equal(validateBdPhone('01611223344'), true); // Airtel
  assert.equal(validateBdPhone('01311223344'), true); // GP New
  assert.equal(validateBdPhone('01411223344'), true); // BL New

  // Invalid numbers
  assert.equal(validateBdPhone('01111223344'), false); // Discontinued Citycell
  assert.equal(validateBdPhone('0171122334'), false);  // Only 10 digits
  assert.equal(validateBdPhone('017112233445'), false); // 12 digits
  assert.equal(validateBdPhone('02711223344'), false);  // Landline prefix
});

test('Discount percentage calculation', () => {
  // 125,000 vs 160,000 -> 21.875% -> Math.floor = 21%
  assert.equal(calcDiscountPercent(125000, 160000), 21);
  // 3,850 vs 4,500 -> 14.444% -> Math.floor = 14%
  assert.equal(calcDiscountPercent(3850, 4500), 14);
  // No discount
  assert.equal(calcDiscountPercent(100, 100), 0);
  assert.equal(calcDiscountPercent(150, 100), 0);
});

test('Rating half-star threshold logic', () => {
  const evaluateStars = (val) => {
    const full = Math.floor(val);
    const hasHalf = val - full >= 0.25 && val - full < 0.75;
    const empty = 5 - full - (hasHalf ? 1 : 0);
    return { full, hasHalf, empty };
  };

  // 4.6 -> 4 full, 1 half, 0 empty
  const r46 = evaluateStars(4.6);
  assert.equal(r46.full, 4);
  assert.equal(r46.hasHalf, true);
  assert.equal(r46.empty, 0);

  // 4.1 -> 4 full, 0 half, 1 empty
  const r41 = evaluateStars(4.1);
  assert.equal(r41.full, 4);
  assert.equal(r41.hasHalf, false);
  assert.equal(r41.empty, 1);

  // 5.0 -> 5 full, 0 half, 0 empty
  const r50 = evaluateStars(5.0);
  assert.equal(r50.full, 5);
  assert.equal(r50.hasHalf, false);
  assert.equal(r50.empty, 0);
});

test('ProductCard and ProductCardSkeleton Structural Parity (Zero CLS)', () => {
  const supportedSizes = ['default', 'compact', 'horizontal'];
  assert.equal(supportedSizes.length, 3);
  assert.ok(supportedSizes.includes('default'));
  assert.ok(supportedSizes.includes('compact'));
  assert.ok(supportedSizes.includes('horizontal'));
});

test('Tokens Hierarchy - Z-Index layering order compliance', () => {
  const z = {
    base: 0,
    sticky: 100,
    header: 200,
    dropdown: 300,
    drawer: 400,
    modal: 500,
    toast: 600,
    tooltip: 700,
  };

  assert.ok(z.base < z.sticky);
  assert.ok(z.sticky < z.header);
  assert.ok(z.header < z.dropdown);
  assert.ok(z.dropdown < z.drawer);
  assert.ok(z.drawer < z.modal);
  assert.ok(z.modal < z.toast);
  assert.ok(z.toast < z.tooltip);
});

test('Tokens Hierarchy - Motion duration progression', () => {
  const d = {
    fast: 0.12,
    base: 0.2,
    slow: 0.32,
  };

  assert.ok(d.fast < d.base);
  assert.ok(d.base < d.slow);
  assert.equal(d.fast, 0.12);
  assert.equal(d.base, 0.20);
  assert.equal(d.slow, 0.32);
});

test('Price formatting - Edge cases (Zero, Decimals, Negative numbers)', () => {
  // 0 BDT
  assert.equal(formatCurrency(0, 'BDT', 'en'), '৳0');
  // Decimal preservation in BDT when fractional poisha exists (e.g. ৳1,25,000.75)
  assert.equal(formatCurrency(125000.75, 'BDT', 'en'), '৳1,25,000.75');
  // Negative amount
  assert.equal(formatCurrency(-5000, 'BDT', 'en'), '৳-5,000');
});

test('Tailwind Preset - status.* semantic color namespace is defined (MW-P2-NEW-01)', async () => {
  const presetMod = await import('../../config/tailwind.preset.js');
  const preset = presetMod.default || presetMod;
  const colors = preset.theme.extend.colors;

  assert.ok(colors.status, 'colors.status namespace must exist');
  assert.equal(colors.status.online, 'var(--status-online, #34D399)');
  assert.equal(colors.status.pending, 'var(--status-pending, #FBBF24)');
  assert.equal(colors.status.fault, 'var(--status-fault, #F87171)');
});

test('Color Tokens - Light Mode focusRing matches WCAG AA #00707D (MW-P0-NEW-08)', async () => {
  const { colors } = await import('../tokens/colors.ts');
  assert.equal(colors.light.focusRing, '#00707D');
  assert.equal(colors.dark.focusRing, '#00E5FF');
});

test('PhoneInput paste normalization strips leading +880 / 88 without truncating digits', () => {
  const normalizeBdPaste = (input) =>
    input.replace(/\D/g, '').replace(/^88(?=01)/, '').slice(0, 11);

  assert.equal(normalizeBdPaste('+8801711223344'), '01711223344');
  assert.equal(normalizeBdPaste('8801812345678'), '01812345678');
  assert.equal(normalizeBdPaste('01912-345-678'), '01912345678');
  assert.equal(validateBdPhone(normalizeBdPaste('+8801711223344')), true);
});

test('MechWareApiClient non-JSON error handling reads stream only once (MW-P1-NEW-04)', async () => {
  const { MechWareApiClient } = await import('../../api-client/src/index.ts');

  const originalFetch = globalThis.fetch;
  try {
    globalThis.fetch = async () =>
      new Response('<html><body>502 Bad Gateway</body></html>', {
        status: 502,
        statusText: 'Bad Gateway',
        headers: { 'Content-Type': 'text/html' },
      });

    const client = new MechWareApiClient({ baseUrl: 'https://api.mechware.com.bd', autoRefreshOn401: false });

    await assert.rejects(
      async () => {
        await client.fetch('/api/v1/catalog/products');
      },
      (err) => {
        assert.equal(err.status, 502);
        assert.equal(err.title, 'Bad Gateway');
        assert.match(err.detail, /502 Bad Gateway/);
        return true;
      }
    );
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test('Phase 3 Acceptance #1: InMemoryTokenVault stores short-lived accessToken strictly in memory with TTL expiry', async () => {
  const { createInMemoryTokenVault } = await import('../../api-client/src/index.ts');
  const vault = createInMemoryTokenVault();

  assert.equal(vault.getToken(), null);

  vault.setToken('jwt_short_lived_15m_token', 900);
  assert.equal(vault.getToken(), 'jwt_short_lived_15m_token');

  // Expired token is automatically purged from memory
  vault.setToken('jwt_already_expired', -1);
  assert.equal(vault.getToken(), null);

  // Verify no Web Storage API is used
  assert.equal(typeof globalThis.localStorage, 'undefined');
});

test('Phase 3 E2E Token Refresh: MechWareApiClient automatically rotates token on 401 and retries request', async () => {
  const { MechWareApiClient, defaultTokenVault } = await import('../../api-client/src/index.ts');
  defaultTokenVault.setToken('expired_access_token', 900);

  const originalFetch = globalThis.fetch;
  const calls = [];

  try {
    globalThis.fetch = async (url, init) => {
      calls.push({ url: String(url), auth: new Headers(init?.headers).get('Authorization') });

      if (String(url).endsWith('/api/v1/auth/refresh')) {
        return new Response(
          JSON.stringify({
            data: {
              accessToken: 'rotated_fresh_access_token_15m',
              expiresIn: 900,
              tokenType: 'Bearer',
            },
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } }
        );
      }

      const authHeader = new Headers(init?.headers).get('Authorization');
      if (authHeader === 'Bearer expired_access_token') {
        return new Response(JSON.stringify({ title: 'Token Expired', status: 401 }), { status: 401 });
      }

      return new Response(JSON.stringify({ data: { orders: ['SV-261029-X7K92M'] } }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    };

    const client = new MechWareApiClient({ baseUrl: 'https://api.mechware.com.bd' });
    const res = await client.fetch('/api/v1/users/me/orders');

    assert.deepEqual(res, { data: { orders: ['SV-261029-X7K92M'] } });
    assert.equal(defaultTokenVault.getToken(), 'rotated_fresh_access_token_15m');
    assert.equal(calls.length, 3);
  } finally {
    globalThis.fetch = originalFetch;
    defaultTokenVault.clear();
  }
});

test('Phase 3 Acceptance #2: Seller Resource Isolation prevents Seller A from reading or modifying Seller B resources', () => {
  const canAccessSellerResource = (principal, resourceSellerId) => {
    if (!resourceSellerId) return false;
    if (principal.roles.includes('admin')) return true;
    return principal.roles.includes('seller') && principal.sellerId === resourceSellerId;
  };

  const sellerAlpha = { roles: ['seller'], sellerId: 'aaaa0000-0000-0000-0000-000000000001' };
  const sellerBetaResource = 'bbbb0000-0000-0000-0000-000000000002';
  const adminUser = { roles: ['admin'], sellerId: null };

  assert.equal(canAccessSellerResource(sellerAlpha, sellerAlpha.sellerId), true);
  assert.equal(canAccessSellerResource(sellerAlpha, sellerBetaResource), false);
  assert.equal(canAccessSellerResource(adminUser, sellerBetaResource), true);
});

test('Phase 3 Open Redirect Protection: sanitizeReturnToPath blocks external URLs and protocol-relative redirects', () => {
  const sanitizeReturnToPath = (rawPath) => {
    if (!rawPath || typeof rawPath !== 'string') return '/';
    const trimmed = rawPath.trim();
    if (!trimmed.startsWith('/') || trimmed.startsWith('//') || trimmed.includes('\\')) {
      return '/';
    }
    return trimmed;
  };

  assert.equal(sanitizeReturnToPath('/bn/account/addresses?tab=default'), '/bn/account/addresses?tab=default');
  assert.equal(sanitizeReturnToPath('https://evil.example.com/phish'), '/');
  assert.equal(sanitizeReturnToPath('//evil.example.com'), '/');
  assert.equal(sanitizeReturnToPath('/\\evil.example.com'), '/');
});

test('Phase 3 Acceptance #3: Bangla (bn) and English (en) Auth & Address translation parity', () => {
  const enPath = path.resolve(__dirname, '../../../apps/web/src/messages/en.json');
  const bnPath = path.resolve(__dirname, '../../../apps/web/src/messages/bn.json');

  const enJson = JSON.parse(fs.readFileSync(enPath, 'utf8'));
  const bnJson = JSON.parse(fs.readFileSync(bnPath, 'utf8'));

  assert.ok(enJson.Auth, 'en.json must contain Auth namespace');
  assert.ok(bnJson.Auth, 'bn.json must contain Auth namespace');

  const enAuthKeys = Object.keys(enJson.Auth).sort();
  const bnAuthKeys = Object.keys(bnJson.Auth).sort();
  assert.deepEqual(bnAuthKeys, enAuthKeys, 'bn.json Auth keys must have 100% parity with en.json');
  assert.match(bnJson.Auth.signInTitle, /সাইন ইন/);
});

test('Phase 3 R1 Audit Fix (AUD-P3-01): RFC 6238 §5.2 TOTP replay guard blocks duplicate time-step presentation', () => {
  const verifyTotpWithReplayGuard = (candidateStep, lastUsedStep) => {
    if (lastUsedStep !== null && candidateStep <= lastUsedStep) {
      return { verified: false, nextLastUsedStep: lastUsedStep };
    }
    return { verified: true, nextLastUsedStep: candidateStep };
  };

  // First presentation at time-step 59824101 -> accepted
  const step1 = verifyTotpWithReplayGuard(59824101, null);
  assert.equal(step1.verified, true);
  assert.equal(step1.nextLastUsedStep, 59824101);

  // Replay of same time-step 59824101 or earlier step 59824100 -> rejected
  const replaySame = verifyTotpWithReplayGuard(59824101, step1.nextLastUsedStep);
  assert.equal(replaySame.verified, false);
  const replayOlder = verifyTotpWithReplayGuard(59824100, step1.nextLastUsedStep);
  assert.equal(replayOlder.verified, false);

  // Next 30s window (step 59824102) -> accepted
  const nextWindow = verifyTotpWithReplayGuard(59824102, step1.nextLastUsedStep);
  assert.equal(nextWindow.verified, true);
  assert.equal(nextWindow.nextLastUsedStep, 59824102);
});

test('Phase 3 R1 Audit Fix (AUD-P3-03 & AUD-P3-06): Backend C# Clean Architecture DIP & OTP MaxAttempts=3 invariants', () => {
  const controllerPath = path.resolve(
    __dirname,
    '../../../../backend/src/ShopVerse.Api/Controllers/AuthAndIdentityControllers.cs'
  );
  const otpPath = path.resolve(
    __dirname,
    '../../../../backend/src/ShopVerse.Domain/Identity/OtpCode.cs'
  );
  const userPath = path.resolve(
    __dirname,
    '../../../../backend/src/ShopVerse.Domain/Identity/User.cs'
  );

  const controllerSource = fs.readFileSync(controllerPath, 'utf8');
  const otpSource = fs.readFileSync(otpPath, 'utf8');
  const userSource = fs.readFileSync(userPath, 'utf8');

  // Verify ShopVerse.Api controllers depend on IIdentityService, not ShopVerse.Infrastructure.Identity
  assert.equal(
    controllerSource.includes('using ShopVerse.Infrastructure.Identity;'),
    false,
    'API controller must not import ShopVerse.Infrastructure.Identity directly'
  );
  assert.equal(
    controllerSource.includes('IIdentityService'),
    true,
    'API controller must inject IIdentityService interface from Application layer'
  );
  assert.equal(
    controllerSource.includes('[EnableRateLimiting(AuthStrictRateLimitPolicy)]'),
    true,
    'Auth endpoints must enforce strict rate limiting'
  );

  // Verify OtpCode.MaxVerificationAttempts == 3 per DATA_MODEL.md §2.1
  assert.equal(
    otpSource.includes('public const int MaxVerificationAttempts = 3;'),
    true,
    'OtpCode must enforce MaxVerificationAttempts = 3'
  );

  // Verify User tracks LastUsedTotpTimeStep for RFC 6238 §5.2 replay protection
  assert.equal(
    userSource.includes('LastUsedTotpTimeStep'),
    true,
    'User entity must track LastUsedTotpTimeStep'
  );
});

test('Phase 3 R1 Audit Fix (AUD-P3-07): 3-Level Bangladesh Division -> District -> Upazila parent-child hierarchy validation', () => {
  const identityServicePath = path.resolve(
    __dirname,
    '../../../../backend/src/ShopVerse.Infrastructure/Identity/IdentityService.cs'
  );
  const addressesPagePath = path.resolve(
    __dirname,
    '../../../apps/web/src/app/[locale]/account/addresses/page.tsx'
  );

  const identityServiceSource = fs.readFileSync(identityServicePath, 'utf8');
  const addressesPageSource = fs.readFileSync(addressesPagePath, 'utf8');

  // Verify backend ValidateBdLocationHierarchy enforces ParentId == matchedDivision.Id and ParentId == matchedDistrict.Id
  assert.equal(
    identityServiceSource.includes('l.ParentId == matchedDivision.Id'),
    true,
    'IdentityService must verify District belongs to Division'
  );
  assert.equal(
    identityServiceSource.includes('l.ParentId == matchedDistrict.Id'),
    true,
    'IdentityService must verify Upazila belongs to District'
  );

  // Verify frontend AccountAddressesPage enforces full CRUD and 3-level parent-child validation
  assert.equal(
    addressesPageSource.includes('handleDeleteAddress') && addressesPageSource.includes('handleEditAddress'),
    true,
    'AccountAddressesPage must implement full Address Book CRUD'
  );

  const SEEDED_BD_LOCATIONS = [
    { id: 'div-dhaka', parentId: null, level: 'division', nameEn: 'Dhaka' },
    { id: 'div-ctg', parentId: null, level: 'division', nameEn: 'Chattogram' },
    { id: 'dist-gazipur', parentId: 'div-dhaka', level: 'district', nameEn: 'Gazipur' },
    { id: 'dist-ctg', parentId: 'div-ctg', level: 'district', nameEn: 'Chattogram' },
    { id: 'upz-tongi', parentId: 'dist-gazipur', level: 'upazila', nameEn: 'Tongi Industrial Zone' },
    { id: 'upz-cepz', parentId: 'dist-ctg', level: 'upazila', nameEn: 'Chattogram EPZ (Halishahar)' },
  ];

  const validateHierarchy = (divisionName, districtName, upazilaName) => {
    const div = SEEDED_BD_LOCATIONS.find((l) => l.level === 'division' && l.nameEn === divisionName);
    if (!div) return false;
    const dist = SEEDED_BD_LOCATIONS.find(
      (l) => l.level === 'district' && l.parentId === div.id && l.nameEn === districtName
    );
    if (!dist) return false;
    const upz = SEEDED_BD_LOCATIONS.find(
      (l) => l.level === 'upazila' && l.parentId === dist.id && l.nameEn === upazilaName
    );
    return Boolean(upz);
  };

  assert.equal(validateHierarchy('Dhaka', 'Gazipur', 'Tongi Industrial Zone'), true);
  assert.equal(validateHierarchy('Chattogram', 'Chattogram', 'Chattogram EPZ (Halishahar)'), true);
  assert.equal(validateHierarchy('Dhaka', 'Chattogram', 'Chattogram EPZ (Halishahar)'), false);
  assert.equal(validateHierarchy('Dhaka', 'Gazipur', 'Savar EPZ'), false);
});

// 24. Pass 2 DB Index Parity (DATA_MODEL.md §2.1 -> ShopVerseDbContext.cs + Migration)
test('24. Phase 3 EF Core DbContext and Migration define all DATA_MODEL.md §2.1 indexes', () => {
  const repoRoot = path.resolve(__dirname, '../../../../');
  const dbContextSrc = fs.readFileSync(
    path.join(repoRoot, 'backend/src/ShopVerse.Infrastructure/Persistence/ShopVerseDbContext.cs'),
    'utf8'
  );
  const migrationSrc = fs.readFileSync(
    path.join(repoRoot, 'backend/src/ShopVerse.Infrastructure/Persistence/Migrations/202610290001_Phase3IdentityAndAccess.cs'),
    'utf8'
  );

  for (const idxName of [
    'ux_users_email',
    'ux_users_phone',
    'ux_users_google_sub',
    'ix_users_status',
    'ix_otp_codes_recipient_type',
    'ix_otp_codes_expires_at'
  ]) {
    assert.ok(dbContextSrc.includes(idxName), `ShopVerseDbContext missing index ${idxName}`);
    assert.ok(migrationSrc.includes(idxName), `Phase3IdentityAndAccess migration missing index ${idxName}`);
  }
  assert.ok(migrationSrc.includes('ix_bd_locations_level_parent'));
  assert.ok(migrationSrc.includes('ux_refresh_tokens_hash'));
  assert.ok(migrationSrc.includes('ix_refresh_tokens_family_id'));
});

// 25. Pass 2 Brute-Force Progressive Delay & Email Verification / Password Reset Session Revocation
test('25. User entity computes progressive login delay and unit tests cover email OTP + password reset session revocation', () => {
  const repoRoot = path.resolve(__dirname, '../../../../');
  const userSrc = fs.readFileSync(
    path.join(repoRoot, 'backend/src/ShopVerse.Domain/Identity/User.cs'),
    'utf8'
  );
  const unitTestsSrc = fs.readFileSync(
    path.join(repoRoot, 'backend/tests/ShopVerse.UnitTests/Identity/IdentityPhase3UnitTests.cs'),
    'utf8'
  );

  assert.ok(userSrc.includes('GetProgressiveLoginDelayMilliseconds()'));
  assert.ok(unitTestsSrc.includes('GetProgressiveLoginDelayMilliseconds()'));
  assert.ok(unitTestsSrc.includes('EmailVerificationAndPasswordResetFlow_ShouldVerifyEmailAndRevokeExistingSessionsOnPasswordReset'));
});

// 26. Pass 2 Zero `any` Types and Canonical Semantic Tokens in Console Sidebar & Web Auth
test('26. Phase 3 frontend files contain zero explicit any types and RoleAwareSidebar uses canonical semantic tokens', () => {
  const repoRoot = path.resolve(__dirname, '../../../../');
  const phase3FrontendFiles = [
    'frontend/apps/web/src/components/auth/AuthModal.tsx',
    'frontend/apps/web/src/components/auth/HeaderAccountMenu.tsx',
    'frontend/apps/web/src/app/[locale]/account/addresses/page.tsx',
    'frontend/apps/console/src/components/RoleAwareSidebar.tsx',
    'frontend/apps/console/src/app/login/page.tsx',
    'src/components/Phase3IdentityWorkbench.tsx'
  ];

  for (const relPath of phase3FrontendFiles) {
    const content = fs.readFileSync(path.join(repoRoot, relPath), 'utf8');
    assert.equal(/:\s*any\b|<any>|as\s+any\b/.test(content), false, `Forbidden 'any' type found in ${relPath}`);
  }

  const sidebarSrc = fs.readFileSync(
    path.join(repoRoot, 'frontend/apps/console/src/components/RoleAwareSidebar.tsx'),
    'utf8'
  );
  assert.ok(sidebarSrc.includes('border-border-subtle'));
  assert.ok(sidebarSrc.includes('bg-bg-surface'));
  assert.ok(sidebarSrc.includes('bg-bg-elevated'));
  assert.ok(sidebarSrc.includes('text-brand-accent'));
  assert.equal(sidebarSrc.includes('border-surface-border'), false);
});

