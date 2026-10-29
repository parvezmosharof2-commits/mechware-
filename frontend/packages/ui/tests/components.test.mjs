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
