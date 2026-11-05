import type { NextConfig } from 'next';
import { securityHeaders } from '@mechware/config/security-headers';

// The Content-Security-Policy carries a per-request nonce, so middleware.ts sets it; these are the static headers.
const headers = securityHeaders();

const nextConfig: NextConfig = {
  reactStrictMode: true,
  poweredByHeader: false,
  transpilePackages: ['@mechware/ui', '@mechware/api-client'],
  async headers() {
    return [{ source: '/:path*', headers }];
  }
};

export default nextConfig;
