import { NextRequest, NextResponse } from 'next/server';
import createMiddleware from 'next-intl/middleware';
import { applyNonceCsp, isAssetOrApiPath } from '@mechware/config/security-headers';

const intlMiddleware = createMiddleware({
  locales: ['en', 'bn'],
  defaultLocale: 'en',
  localePrefix: 'as-needed'
});

const PROTECTED_PATH_SEGMENTS = ['/account', '/checkout', '/orders'];

/**
 * Sanitize returnTo URL to prevent Open Redirect attacks (OWASP Top 10).
 * Only allows relative internal paths starting with a single slash.
 */
export function sanitizeReturnToPath(rawPath: string | null | undefined): string {
  if (!rawPath || typeof rawPath !== 'string') {
    return '/';
  }
  const trimmed = rawPath.trim();
  if (!trimmed.startsWith('/') || trimmed.startsWith('//') || trimmed.includes('\\')) {
    return '/';
  }
  return trimmed;
}

export default function middleware(request: NextRequest) {
  // A per-request CSP nonce, so only scripts this server rendered run (threat model M5). The request carries it to the
  // renderer; the response carries the policy to the browser.
  const requestHeaders = new Headers(request.headers);
  const csp = applyNonceCsp(requestHeaders);
  const forward = () => NextResponse.next({ request: { headers: requestHeaders } });

  let response: NextResponse;
  if (isAssetOrApiPath(request.nextUrl.pathname)) {
    response = forward();
  } else {
    response = route(new NextRequest(request, { headers: requestHeaders }));
    // next-intl forwards the request headers when it routes, but not on every path (e.g. a URL it cannot decode). A
    // page rendered without the nonce would have all its scripts blocked, so such a pass-through is redone here.
    if (response.headers.get('x-middleware-next') && !response.headers.has('x-middleware-override-headers')) {
      response = forward();
    }
  }
  response.headers.set('Content-Security-Policy', csp);
  return response;
}

function route(request: NextRequest): NextResponse {
  const { pathname, search } = request.nextUrl;

  const isProtectedRoute = PROTECTED_PATH_SEGMENTS.some(
    (segment) =>
      pathname === segment ||
      pathname.startsWith(`${segment}/`) ||
      pathname === `/bn${segment}` ||
      pathname.startsWith(`/bn${segment}/`) ||
      pathname === `/en${segment}` ||
      pathname.startsWith(`/en${segment}/`)
  );

  if (isProtectedRoute) {
    // The refresh cookie is scoped to the API path and never reaches this route, so we read the
    // credential-free marker set by AuthProvider. This is a UX redirect only; the API authorises.
    const hasSessionMarker = request.cookies.has('mw_session');
    if (!hasSessionMarker) {
      const isBangla = pathname === '/bn' || pathname.startsWith('/bn/');
      const loginPath = isBangla ? '/bn/auth/login' : '/auth/login';
      const returnTo = sanitizeReturnToPath(`${pathname}${search}`);

      const loginUrl = new URL(loginPath, request.url);
      loginUrl.searchParams.set('returnTo', returnTo);
      return NextResponse.redirect(loginUrl);
    }
  }

  return intlMiddleware(request);
}

export const config = {
  // Only Next's own hashed build output skips the middleware; every page and public file gets the CSP.
  matcher: ['/((?!_next/static|_next/image).*)']
};
