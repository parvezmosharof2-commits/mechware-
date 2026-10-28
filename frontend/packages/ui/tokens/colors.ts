export interface ColorPalette {
  bg: string;
  bgSubtle: string;
  bgMuted: string;
  surface: string;
  border: string;
  borderStrong: string;
  text: string;
  textMuted: string;
  textSubtle: string;
  primary: string;
  primaryFg: string;
  accent: string;
  accentFg: string;
  accentHover: string;
  brandNavy: string;
  brandBlue: string;
  deal: string;
  fault: string;
  success: string;
  online: string;
  warning: string;
  pending: string;
  focusRing: string;
}

export const colors: { dark: ColorPalette; light: ColorPalette } = {
  dark: {
    bg: '#060B18',
    bgSubtle: '#0A1128',
    bgMuted: '#101B3B',
    surface: '#0D1635',
    border: '#1B2B55',
    borderStrong: '#2A4078',
    text: '#F8FAFC',
    textMuted: '#94A3B8',
    textSubtle: '#64748B',
    primary: '#F8FAFC',
    primaryFg: '#060B18',
    accent: '#00E5FF',
    accentFg: '#060B18',
    accentHover: '#38EFFF',
    brandNavy: '#0F3E7D',
    brandBlue: '#1A5CB0',
    deal: '#F87171',
    fault: '#F87171',
    success: '#34D399',
    online: '#34D399',
    warning: '#FBBF24',
    pending: '#FBBF24',
    focusRing: '#00E5FF',
  },
  light: {
    bg: '#F8FAFC',
    bgSubtle: '#F1F5F9',
    bgMuted: '#E2E8F0',
    surface: '#FFFFFF',
    border: '#CBD5E1',
    borderStrong: '#94A3B8',
    text: '#0F172A',
    textMuted: '#475569',
    textSubtle: '#64748B',
    primary: '#0A1128',
    primaryFg: '#FFFFFF',
    accent: '#00707D',
    accentFg: '#FFFFFF',
    accentHover: '#005C66',
    brandNavy: '#0F3E7D',
    brandBlue: '#1A5CB0',
    deal: '#DC2626',
    fault: '#DC2626',
    success: '#10B981',
    online: '#10B981',
    warning: '#B45309',
    pending: '#B45309',
    focusRing: '#00A8B5',
  },
};

export const scales = {
  accent: {
    50: '#E0FBFC',
    100: '#B8F6FA',
    200: '#7CEEF7',
    300: '#38EFFF',
    400: '#00E5FF',
    500: '#00C3D9',
    600: '#009BAF',
    700: '#007888',
    800: '#005864',
    900: '#003E47',
    950: '#00252C',
  },
  neutral: {
    50: '#F8FAFC',
    100: '#F1F5F9',
    200: '#E2E8F0',
    300: '#CBD5E1',
    400: '#94A3B8',
    500: '#64748B',
    600: '#475569',
    700: '#334155',
    800: '#1E293B',
    900: '#0F172A',
    950: '#060B18',
  },
  navy: {
    50: '#EFF6FF',
    100: '#DBEAFE',
    200: '#BFDBFE',
    300: '#93C5FD',
    400: '#60A5FA',
    500: '#3B82F6',
    600: '#1A5CB0',
    700: '#0F3E7D',
    800: '#1E3A8A',
    900: '#172554',
    950: '#0A1128',
  },
};

// 6-color accessible chart palette with high contrast and color-blind friendliness
export const chartPalette = [
  '#00E5FF', // Electric Cyan / Mech Teal
  '#38BDF8', // Sky Blue
  '#818CF8', // Indigo / Violet
  '#34D399', // Emerald Green
  '#FBBF24', // Amber
  '#F87171', // Coral Red
] as const;
