/**
 * HTTP security headers for the Next.js apps (web and console).
 *
 * Finding #7 of the 2026-11-04 independent test (threat model M5): the API sent strict headers, but the two Next.js apps
 * sent none, so a page could be framed (clickjacking), sniffed, or load script from anywhere.
 *
 * The CSP still allows inline scripts and styles, because Next.js injects inline bootstrap scripts and the UI uses inline
 * styles. Replacing 'unsafe-inline' with per-request nonces is planned for the hardening sprint (MASTER_PLAN.md, 17-18).
 * Everything else is locked down: no plugins, no framing, no foreign forms, and script / API calls only to this app, the
 * MECHWARE API and, when Google sign-in is configured, Google's sign-in endpoints.
 *
 * @param {{ apiBaseUrl?: string, googleSignIn?: boolean, isDev?: boolean, allowPopups?: boolean }} options
 * @returns {{ key: string, value: string }[]}
 */
function securityHeaders(options = {}) {
  const { apiBaseUrl = 'http://localhost:5000', googleSignIn = false, isDev = false, allowPopups = false } = options;
  const api = new URL(apiBaseUrl).origin;
  const google = googleSignIn ? ['https://accounts.google.com'] : [];

  const csp = [
    "default-src 'self'",
    // 'unsafe-eval' only in development, where React Refresh needs it.
    ["script-src 'self' 'unsafe-inline'", isDev ? "'unsafe-eval'" : '', ...google].filter(Boolean).join(' '),
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

  return [
    { key: 'Content-Security-Policy', value: csp },
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

module.exports = { securityHeaders };
