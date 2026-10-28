export const typography = {
  fonts: {
    body: 'var(--font-inter), Inter, -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif',
    display: 'var(--font-jakarta), "Plus Jakarta Sans", Inter, -apple-system, sans-serif',
    bengali: 'var(--font-hind), "Hind Siliguri", "Noto Sans Bengali", sans-serif',
  },
  sizes: {
    display: {
      fontSize: '3.5rem', // 56px
      lineHeight: '4rem', // 64px
      fontWeight: '800',
      letterSpacing: '-0.02em',
    },
    h1: {
      fontSize: '2.5rem', // 40px
      lineHeight: '3rem', // 48px
      fontWeight: '700',
      letterSpacing: '-0.02em',
    },
    h2: {
      fontSize: '2rem', // 32px
      lineHeight: '2.5rem', // 40px
      fontWeight: '700',
      letterSpacing: '-0.02em',
    },
    h3: {
      fontSize: '1.5rem', // 24px
      lineHeight: '2rem', // 32px
      fontWeight: '600',
      letterSpacing: '-0.01em',
    },
    h4: {
      fontSize: '1.25rem', // 20px
      lineHeight: '1.75rem', // 28px
      fontWeight: '600',
      letterSpacing: '0',
    },
    bodyLg: {
      fontSize: '1.125rem', // 18px
      lineHeight: '1.75rem', // 28px
      fontWeight: '400',
    },
    body: {
      fontSize: '1rem', // 16px
      lineHeight: '1.5rem', // 24px
      fontWeight: '400',
    },
    bodySm: {
      fontSize: '0.875rem', // 14px
      lineHeight: '1.25rem', // 20px
      fontWeight: '400',
    },
    caption: {
      fontSize: '0.75rem', // 12px
      lineHeight: '1rem', // 16px
      fontWeight: '500',
      letterSpacing: '0.04em',
    },
    priceLg: {
      fontSize: '2rem', // 32px
      lineHeight: '2.5rem', // 40px
      fontWeight: '700',
      fontVariantNumeric: 'tabular-nums',
    },
    price: {
      fontSize: '1.125rem', // 18px
      lineHeight: '1.5rem', // 24px
      fontWeight: '700',
      fontVariantNumeric: 'tabular-nums',
    },
  },
  readingMaxWidth: '70ch',
} as const;
