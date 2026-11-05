import type { Metadata } from 'next';
import { Inter, Plus_Jakarta_Sans } from 'next/font/google';
import { ToastProvider } from '@mechware/ui';
import { AuthProvider } from '../lib/auth';
import './globals.css';

const inter = Inter({
  subsets: ['latin'],
  variable: '--font-inter',
  display: 'swap'
});

const plusJakarta = Plus_Jakarta_Sans({
  subsets: ['latin'],
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
