import { NextRequest, NextResponse } from 'next/server';
import { applyNonceCsp, isAssetOrApiPath } from '@mechware/config/security-headers';

/**
 * Two jobs:
 * - every response gets a CSP with a fresh nonce, so only scripts this server rendered run (threat model M5);
 * - pages: signed-out visitors go to /login before any console page renders. This is a UX redirect only: the API
 *   authorises every request itself, and this marker proves nothing (see AuthProvider).
 */
export default function middleware(request: NextRequest) {
  const requestHeaders = new Headers(request.headers);
  const csp = applyNonceCsp(requestHeaders);

  const { pathname } = request.nextUrl;
  const isLogin = pathname === '/login' || pathname.startsWith('/login/');
  const hasMarker = request.cookies.has('mw_console_session');

  const response =
    !isAssetOrApiPath(pathname) && !hasMarker && !isLogin
      ? NextResponse.redirect(new URL('/login', request.url))
      : NextResponse.next({ request: { headers: requestHeaders } });
  response.headers.set('Content-Security-Policy', csp);
  return response;
}

export const config = {
  // Only Next's own hashed build output skips the middleware; every page and public file gets the CSP.
  matcher: ['/((?!_next/static|_next/image).*)']
};
