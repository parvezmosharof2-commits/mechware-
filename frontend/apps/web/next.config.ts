import type { NextConfig } from 'next';
import createNextIntlPlugin from 'next-intl/plugin';
import { securityHeaders } from '@mechware/config/security-headers';

const withNextIntl = createNextIntlPlugin('./src/i18n/request.ts');

// The Content-Security-Policy carries a per-request nonce, so middleware.ts sets it; these are the static headers.
const headers = securityHeaders({ googleSignIn: Boolean(process.env.NEXT_PUBLIC_GOOGLE_CLIENT_ID) });

const nextConfig: NextConfig = {
  reactStrictMode: true,
  poweredByHeader: false,
  // Next 15 streams metadata into <body> for browser user agents. Lighthouse and crawlers need description, canonical,
  // hreflang and the legal pages' noindex in <head>, so metadata blocks the first byte for every user agent.
  htmlLimitedBots: /.*/,
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
