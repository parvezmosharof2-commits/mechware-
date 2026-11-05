import type { NextConfig } from 'next';
import createNextIntlPlugin from 'next-intl/plugin';
import { securityHeaders } from '@mechware/config/security-headers';

const withNextIntl = createNextIntlPlugin('./src/i18n/request.ts');

// The Content-Security-Policy carries a per-request nonce, so middleware.ts sets it; these are the static headers.
const headers = securityHeaders({ googleSignIn: Boolean(process.env.NEXT_PUBLIC_GOOGLE_CLIENT_ID) });

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
