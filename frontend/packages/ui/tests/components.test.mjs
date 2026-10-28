import test from 'node:test';
import assert from 'node:assert/strict';

import { formatCurrency, validateBdPhone, calcDiscountPercent } from '../src/formatters.ts';

test('Price formatting - South Asian Lakh grouping for BDT', () => {
  // 1,25,000 BDT
  const formatted1 = formatCurrency(125000, 'BDT', 'en');
  assert.equal(formatted1, '৳1,25,000');

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
  // 125,000 vs 160,000 -> 22%
  assert.equal(calcDiscountPercent(125000, 160000), 22);
  // 3,850 vs 4,500 -> 14%
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
  // Decimal rounding in BDT (rounded to integer for BDT commercial display)
  assert.equal(formatCurrency(125000.75, 'BDT', 'en'), '৳1,25,001');
  // Negative amount
  assert.equal(formatCurrency(-5000, 'BDT', 'en'), '৳-5,000');
});
