import type { NextConfig } from 'next';

const nextConfig: NextConfig = {
  reactStrictMode: true,
  transpilePackages: ['@mechware/ui', '@mechware/api-client']
};

export default nextConfig;
