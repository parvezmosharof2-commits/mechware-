import type { Metadata } from 'next';
import localFont from 'next/font/local';
import { NextIntlClientProvider } from 'next-intl';
import { getMessages } from 'next-intl/server';
import { ToastProvider } from '@mechware/ui';
import { AuthProvider } from '../../lib/auth';
import '../globals.css';

// The fonts are kept in the repository (../fonts, SIL Open Font License; from Fontsource 5.3.0), not fetched from Google
// Fonts at build time: a build must not fail because a font server is slow to answer.
const inter = localFont({
  src: '../fonts/inter-latin-wght-normal.woff2',
  weight: '100 900',
  variable: '--font-inter',
  display: 'swap'
});

const plusJakarta = localFont({
  src: '../fonts/plus-jakarta-sans-latin-wght-normal.woff2',
  weight: '200 800',
  variable: '--font-jakarta',
  display: 'swap'
});

const hindSiliguri = localFont({
  src: [
    { path: '../fonts/hind-siliguri-bengali-400-normal.woff2', weight: '400', style: 'normal' },
    { path: '../fonts/hind-siliguri-bengali-500-normal.woff2', weight: '500', style: 'normal' },
    { path: '../fonts/hind-siliguri-bengali-600-normal.woff2', weight: '600', style: 'normal' },
    { path: '../fonts/hind-siliguri-bengali-700-normal.woff2', weight: '700', style: 'normal' }
  ],
  variable: '--font-hind',
  display: 'swap'
});

// Every page is rendered per request, so Next.js can stamp that request's CSP nonce on its scripts (threat model M5).
export const dynamic = 'force-dynamic';

export const metadata: Metadata = {
  title: 'MECHWARE — YOUR GATEWAY TO INDUSTRY 4.0',
  description: 'Enterprise Industrial Machinery, Automation Marketplace & Engineering Platform | Made in Bangladesh Machinery'
};

export default async function RootLayout({
  children,
  params
}: {
  children: React.ReactNode;
  params: Promise<{ locale: string }>;
}) {
  const { locale } = await params;
  const messages = await getMessages();

  return (
    <html lang={locale} className={`dark ${inter.variable} ${plusJakarta.variable} ${hindSiliguri.variable}`}>
      <body className="font-sans antialiased bg-bg text-text selection:bg-accent selection:text-accent-fg">
        <NextIntlClientProvider messages={messages}>
          <AuthProvider>
            <ToastProvider>{children}</ToastProvider>
          </AuthProvider>
        </NextIntlClientProvider>
      </body>
    </html>
  );
}
