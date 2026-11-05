import type { NextConfig } from 'next';
import { securityHeaders } from '@mechware/config/security-headers';

const headers = securityHeaders({
  apiBaseUrl: process.env.NEXT_PUBLIC_API_BASE_URL ?? 'http://localhost:5000',
  isDev: process.env.NODE_ENV !== 'production'
});

const nextConfig: NextConfig = {
  reactStrictMode: true,
  poweredByHeader: false,
  transpilePackages: ['@mechware/ui', '@mechware/api-client'],
  async headers() {
    return [{ source: '/:path*', headers }];
  }
};

export default nextConfig;
