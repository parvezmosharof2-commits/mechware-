export const spacing = {
  scale: {
    0: '0px',
    0.5: '2px',
    1: '4px',
    2: '8px',
    3: '12px',
    4: '16px',
    5: '20px',
    6: '24px',
    8: '32px',
    10: '40px',
    12: '48px',
    16: '64px',
    20: '80px',
    24: '96px',
    32: '128px',
  },
  breakpoints: {
    sm: '640px',
    md: '768px',
    lg: '1024px',
    xl: '1280px',
    '2xl': '1536px',
  },
  container: {
    maxWidth: '1440px',
    padding: {
      mobile: '16px',
      tablet: '24px',
      desktop: '32px',
    },
  },
  grid: {
    columns: {
      mobile: 4,
      tablet: 8,
      desktop: 12,
    },
    gutter: {
      mobile: '16px',
      tablet: '20px',
      desktop: '24px',
    },
  },
  productGrid: {
    columns: {
      mobile: 2,
      md: 3,
      lg: 4,
      '2xl': 5,
    },
  },
  sectionGap: {
    mobile: '48px',
    desktop: '80px',
  },
} as const;
