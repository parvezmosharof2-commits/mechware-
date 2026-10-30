import { NextRequest, NextResponse } from 'next/server';

/**
 * UX redirect only: signed-out visitors go to /login before any console page renders. The API authorises
 * every request itself; this marker proves nothing (see AuthProvider).
 */
export default function middleware(request: NextRequest) {
  const { pathname } = request.nextUrl;
  const isLogin = pathname === '/login' || pathname.startsWith('/login/');
  const hasMarker = request.cookies.has('mw_console_session');

  if (!hasMarker && !isLogin) {
    return NextResponse.redirect(new URL('/login', request.url));
  }
  return NextResponse.next();
}

export const config = {
  matcher: ['/((?!api|_next|_vercel|.*\\..*).*)']
};
