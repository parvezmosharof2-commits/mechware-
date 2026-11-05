import type { NextConfig } from 'next';
import createNextIntlPlugin from 'next-intl/plugin';
import { securityHeaders } from '@mechware/config/security-headers';

const withNextIntl = createNextIntlPlugin('./src/i18n/request.ts');

const headers = securityHeaders({
  apiBaseUrl: process.env.NEXT_PUBLIC_API_BASE_URL ?? 'http://localhost:5000',
  googleSignIn: Boolean(process.env.NEXT_PUBLIC_GOOGLE_CLIENT_ID),
  isDev: process.env.NODE_ENV !== 'production'
});

const nextConfig: NextConfig = {
  reactStrictMode: true,
  poweredByHeader: false,
  transpilePackages: ['@mechware/ui', '@mechware/api-client'],
  images: {
    remotePatterns: [
      {
        protocol: 'https',
        hostname: '**'
      },
      {
        protocol: 'http',
        hostname: 'localhost'
      }
    ]
  },
  async headers() {
    return [{ source: '/:path*', headers }];
  }
};

export default withNextIntl(nextConfig);
