'use client';

import dynamic from 'next/dynamic';
import { useAuth } from '../../lib/auth';

// Loaded only after hydration and only for a signed-in user: guests never download (or render) the company-price code.
const CompanyPriceNote = dynamic(() => import('./CompanyPriceNote'), { ssr: false });

export function CompanyPriceGate(props: { variantId: string; quantity: number; currency: string }) {
  const { status } = useAuth();
  return status === 'authenticated' ? <CompanyPriceNote {...props} /> : null;
}
