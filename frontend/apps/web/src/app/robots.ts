import type { MetadataRoute } from 'next';
import { absoluteUrl } from '../lib/site-url';

export default function robots(): MetadataRoute.Robots {
  return {
    rules: [
      {
        userAgent: '*',
        allow: '/',
        disallow: ['/account', '/auth', '/api', '/search', '/bn/account', '/bn/auth', '/bn/search']
      }
    ],
    sitemap: absoluteUrl('/sitemap.xml')
  };
}
