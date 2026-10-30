import type { Metadata } from 'next';
import { Inter, Plus_Jakarta_Sans, Hind_Siliguri } from 'next/font/google';
import { NextIntlClientProvider } from 'next-intl';
import { getMessages } from 'next-intl/server';
import { ToastProvider } from '@mechware/ui';
import { AuthProvider } from '../../lib/auth';
import '../globals.css';

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

const hindSiliguri = Hind_Siliguri({
  weight: ['400', '500', '600', '700'],
  subsets: ['bengali'],
  variable: '--font-hind',
  display: 'swap'
});

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
