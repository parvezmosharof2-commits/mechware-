import { mkdirSync } from 'node:fs';
import { chromium } from 'playwright';

export const WEB = process.env.E2E_WEB_URL ?? 'http://localhost:3000';
export const CONSOLE = process.env.E2E_CONSOLE_URL ?? 'http://localhost:3001';
export const API = process.env.E2E_API_URL ?? 'http://localhost:5000';
export const SHOTS = process.env.E2E_SHOTS_DIR ?? 'shots';

/** Development seed accounts of the in-memory identity store (see IdentityService). */
export const USERS = {
  buyer: { email: 'buyer@mechware.com.bd', password: 'BuyerPass#2026!' },
  sellerAlpha: { email: 'seller.alpha@mechware.com.bd', password: 'SellerAlpha#2026!' },
  sellerBeta: { email: 'seller.beta@mechware.com.bd', password: 'SellerBeta#2026!' },
  admin: { email: 'admin@mechware.com.bd', password: 'MechAdmin#2026!', totpSecret: 'JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP' }
};

export function launch() {
  mkdirSync(SHOTS, { recursive: true });
  // PW_CHROMIUM_PATH is only for machines whose browser was installed outside Playwright's own cache.
  return chromium.launch({ executablePath: process.env.PW_CHROMIUM_PATH || undefined });
}

export function reporter() {
  const results = [];
  return {
    check(name, ok, extra = '') {
      results.push(ok);
      console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${extra ? '  -> ' + extra : ''}`);
    },
    finish() {
      const failed = results.filter((r) => !r).length;
      console.log(`\n${results.length - failed}/${results.length} passed`);
      return failed === 0;
    }
  };
}

/** A unique, valid 13-digit BIN per call. */
export function uniqueBin() {
  return String(Date.now()).padStart(13, '0').slice(-13);
}

function base32Decode(s) {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let bits = '';
  for (const c of s.replace(/=+$/, '')) bits += alphabet.indexOf(c).toString(2).padStart(5, '0');
  const bytes = [];
  for (let i = 0; i + 8 <= bits.length; i += 8) bytes.push(parseInt(bits.slice(i, i + 8), 2));
  return Buffer.from(bytes);
}

/** RFC 6238 code (SHA-1, 6 digits, 30 s step). */
export async function totp(secret, now = Date.now()) {
  const { createHmac } = await import('node:crypto');
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(now / 1000 / 30)));
  const h = createHmac('sha1', base32Decode(secret)).update(counter).digest();
  const o = h[h.length - 1] & 15;
  return String((h.readUInt32BE(o) & 0x7fffffff) % 1000000).padStart(6, '0');
}

export async function apiLogin(user) {
  const r = await fetch(`${API}/api/v1/auth/login`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ identifier: user.email, password: user.password })
  });
  return (await r.json()).data.accessToken;
}

export async function apiCall(token, method, path, body) {
  const r = await fetch(`${API}${path}`, {
    method,
    headers: { 'content-type': 'application/json', authorization: `Bearer ${token}` },
    body: body ? JSON.stringify(body) : undefined
  });
  return { status: r.status, json: await r.json().catch(() => ({})) };
}
