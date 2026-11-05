/**
 * HTTP security headers for the Next.js apps (web and console).
 *
 * Finding #7 of the 2026-11-04 independent test (threat model M5): the API sent strict headers, but the two Next.js apps
 * sent none, so a page could be framed (clickjacking), sniffed, or load script from anywhere.
 *
 * Scripts: a fresh nonce per request (set by each app's middleware) with 'strict-dynamic', and no 'unsafe-inline'. An
 * injected <script> or inline event handler therefore does not run, which is what stands between an XSS bug and the
 * in-memory access token. Styles keep 'unsafe-inline' (the UI and next/font use inline styles; CSS cannot run code).
 * Everything else is locked down: no plugins, no framing, no foreign forms, and API calls only to this app, the MECHWARE
 * API and, when Google sign-in is configured, Google's sign-in endpoints.
 *
 * With 'strict-dynamic' a host in script-src means nothing, so Google's sign-in library must be loaded with next/script,
 * which Next.js stamps with the nonce.
 */

/**
 * @param {{ apiBaseUrl?: string, googleSignIn?: boolean, isDev?: boolean, nonce: string }} options
 * @returns {string}
 */
function contentSecurityPolicy(options) {
  const { apiBaseUrl = 'http://localhost:5000', googleSignIn = false, isDev = false, nonce } = options;
  if (!nonce || !/^[A-Za-z0-9+/=_-]{16,}$/.test(nonce)) throw new Error('A random nonce of at least 16 characters is required.');
  const api = new URL(apiBaseUrl).origin;
  const google = googleSignIn ? ['https://accounts.google.com'] : [];

  return [
    "default-src 'self'",
    // 'strict-dynamic' lets the nonced Next.js bootstrap load its chunks; host lists are ignored by modern browsers then.
    // 'unsafe-eval' only in development, where React Refresh needs it.
    [`script-src 'self' 'nonce-${nonce}' 'strict-dynamic'`, isDev ? "'unsafe-eval'" : ''].filter(Boolean).join(' '),
    ["style-src 'self' 'unsafe-inline'", ...google].join(' '),
    "img-src 'self' data: blob: https:",
    "font-src 'self' data:",
    ["connect-src 'self'", api, isDev ? 'ws:' : '', ...google].filter(Boolean).join(' '),
    ['frame-src', ...(google.length ? google : ["'none'"])].join(' '),
    "frame-ancestors 'none'",
    "object-src 'none'",
    "base-uri 'none'",
    "form-action 'self'"
  ].join('; ');
}

/** A fresh, unguessable nonce for one response (Web Crypto, so it works in the edge middleware). */
function createNonce() {
  const bytes = new Uint8Array(18);
  crypto.getRandomValues(bytes);
  return btoa(String.fromCharCode(...bytes));
}

/**
 * The static headers, sent on every response. The CSP is not among them: it carries a per-request nonce, so each app's
 * middleware sets it (see contentSecurityPolicy).
 *
 * @param {{ googleSignIn?: boolean, allowPopups?: boolean }} options
 * @returns {{ key: string, value: string }[]}
 */
function securityHeaders(options = {}) {
  const { googleSignIn = false, allowPopups = false } = options;
  return [
    { key: 'X-Frame-Options', value: 'DENY' },
    { key: 'X-Content-Type-Options', value: 'nosniff' },
    { key: 'Referrer-Policy', value: 'strict-origin-when-cross-origin' },
    { key: 'Permissions-Policy', value: 'accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()' },
    // Ignored by browsers over plain HTTP, so it is harmless on localhost and active once served over HTTPS.
    { key: 'Strict-Transport-Security', value: 'max-age=31536000; includeSubDomains' },
    // Google's sign-in popup must be able to reply to the opener; otherwise keep the window isolated.
    { key: 'Cross-Origin-Opener-Policy', value: allowPopups || googleSignIn ? 'same-origin-allow-popups' : 'same-origin' }
  ];
}

/**
 * Applies a nonce CSP to one middleware pass: the request carries it (Next.js reads the nonce from the request's CSP and
 * stamps it on its own scripts; x-nonce is for any Script the app renders) and the response sends it to the browser.
 *
 * @param {Headers} requestHeaders mutable copy of the incoming request headers
 * @param {{ apiBaseUrl?: string, googleSignIn?: boolean, isDev?: boolean }} options
 * @returns {string} the CSP value to set on the response
 */
function applyNonceCsp(requestHeaders, options = defaultCspOptions()) {
  const nonce = createNonce();
  const csp = contentSecurityPolicy({ ...options, nonce });
  requestHeaders.set('x-nonce', nonce);
  requestHeaders.set('Content-Security-Policy', csp);
  return csp;
}

/** The CSP inputs both apps share, from the build environment, so the two policies cannot drift apart. */
function defaultCspOptions() {
  return {
    apiBaseUrl: process.env.NEXT_PUBLIC_API_BASE_URL ?? 'http://localhost:5000',
    googleSignIn: Boolean(process.env.NEXT_PUBLIC_GOOGLE_CLIENT_ID),
    isDev: process.env.NODE_ENV !== 'production'
  };
}

/**
 * Paths the middleware passes straight through (files, API routes): they still get the CSP, but no locale routing or
 * sign-in redirect. Everything except Next's own hashed build output goes through the middleware, so no page or public
 * file is ever served without a policy.
 */
function isAssetOrApiPath(pathname) {
  return /^\/api(\/|$)/.test(pathname) || /\.[^/]+$/.test(pathname);
}

module.exports = { securityHeaders, applyNonceCsp, defaultCspOptions, isAssetOrApiPath };
