import type { Metadata } from 'next';
import localFont from 'next/font/local';
import { ToastProvider } from '@mechware/ui';
import { AuthProvider } from '../lib/auth';
import './globals.css';

// The fonts are kept in the repository (./fonts, SIL Open Font License; from Fontsource 5.3.0), not fetched from Google
// Fonts at build time: a build must not fail because a font server is slow to answer.
const inter = localFont({
  src: './fonts/inter-latin-wght-normal.woff2',
  weight: '100 900',
  variable: '--font-inter',
  display: 'swap'
});

const plusJakarta = localFont({
  src: './fonts/plus-jakarta-sans-latin-wght-normal.woff2',
  weight: '200 800',
  variable: '--font-jakarta',
  display: 'swap'
});

// Every page is rendered per request, so Next.js can stamp that request's CSP nonce on its scripts (threat model M5).
export const dynamic = 'force-dynamic';

export const metadata: Metadata = {
  title: 'MECHWARE Console — Management & Seller Hub',
  description: 'Operations, Catalog, KYC & Financial Ledger Management'
};

export default function RootLayout({
  children
}: {
  children: React.ReactNode;
}) {
  return (
    <html lang="en" className={`dark ${inter.variable} ${plusJakarta.variable}`}>
      <body className="font-sans antialiased bg-bg text-text">
        <AuthProvider>
          <ToastProvider>{children}</ToastProvider>
        </AuthProvider>
      </body>
    </html>
  );
}
