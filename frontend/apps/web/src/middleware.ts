import { NextRequest, NextResponse } from 'next/server';
import createMiddleware from 'next-intl/middleware';

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
    const hasRefreshCookie = request.cookies.has('refresh_token');
    if (!hasRefreshCookie) {
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
  matcher: ['/((?!api|_next|_vercel|.*\\..*).*)']
};
