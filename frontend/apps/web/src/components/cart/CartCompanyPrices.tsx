'use client';

import * as React from 'react';
import { pickDisplay, type PriceAsk, type PriceDisplay } from '@mechware/api-client/src/company-prices-model';
import { useCompanyPrices } from '../../lib/useCompanyPrices';

export interface CartCompanyPricesResult {
  company: string;
  /** Only lines with a real company price (contract, quantity break or sale) that beats the public one. */
  lines: Map<string, PriceDisplay>;
}

/**
 * Loaded lazily and only for a signed-in user (see the cart page), so guests never download the company-price code.
 * Renders nothing; it reports its result upward. Any failure or "not a company buyer" reports null (public prices only).
 */
export default function CartCompanyPrices({ asks, onResult }: { asks: readonly PriceAsk[]; onResult: (r: CartCompanyPricesResult | null) => void }) {
  const { phase, company, items } = useCompanyPrices(asks);
  React.useEffect(() => {
    if (phase !== 'ready' || !company) return onResult(null);
    const lines = new Map<string, PriceDisplay>();
    for (const [id, item] of items) {
      const d = pickDisplay(item);
      if (d && d.kind !== 'none') lines.set(id, d);
    }
    onResult({ company: company.name, lines });
  }, [phase, company, items, onResult]);
  return null;
}
