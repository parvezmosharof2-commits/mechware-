/**
 * Format currency with proper South Asian Lakh/Crore grouping for BDT or standard Western for USD.
 */
export function formatCurrency(
  val: number,
  currency: 'BDT' | 'USD' = 'BDT',
  locale: string = 'en'
): string {
  const symbol = currency === 'BDT' ? '৳' : '$';

  if (currency === 'BDT') {
    // South Asian Lakh formatting (e.g. 1,25,000)
    const isNegative = val < 0;
    const absVal = Math.abs(val);
    const parts = absVal.toFixed(0).split('.');
    let integerPart = parts[0];

    if (integerPart.length > 3) {
      const lastThree = integerPart.substring(integerPart.length - 3);
      const remaining = integerPart.substring(0, integerPart.length - 3);
      // Group by two digits
      const groupedRemaining = remaining.replace(/\B(?=(\d{2})+(?!\d))/g, ',');
      integerPart = `${groupedRemaining},${lastThree}`;
    }

    if (locale === 'bn') {
      // Bengali numeral conversion
      const banglaDigits = ['০', '১', '২', '৩', '৪', '৫', '৬', '৭', '৮', '৯'];
      integerPart = integerPart.replace(/\d/g, (d) => banglaDigits[parseInt(d, 10)]);
    }

    return `${symbol}${isNegative ? '-' : ''}${integerPart}`;
  }

  // Western currency formatting
  return new Intl.NumberFormat(locale === 'bn' ? 'bn-BD' : 'en-US', {
    style: 'currency',
    currency: currency,
    maximumFractionDigits: 2,
  }).format(val);
}

export function validateBdPhone(phone: string): boolean {
  const cleaned = phone.replace(/\D/g, '');
  return /^01[3-9]\d{8}$/.test(cleaned);
}

export function calcDiscountPercent(price: number, compareAt?: number): number {
  if (!compareAt || compareAt <= price) return 0;
  return Math.round(((compareAt - price) / compareAt) * 100);
}
