import type { Metadata } from 'next';
import { Inter, Plus_Jakarta_Sans } from 'next/font/google';
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
        {children}
      </body>
    </html>
  );
}
