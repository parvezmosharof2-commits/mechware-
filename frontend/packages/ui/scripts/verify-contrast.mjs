const colors = {
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
    focusRing: '#00707D',
  },
};

function hexToRgb(hex) {
  const cleanHex = hex.replace('#', '');
  const r = parseInt(cleanHex.substring(0, 2), 16);
  const g = parseInt(cleanHex.substring(2, 4), 16);
  const b = parseInt(cleanHex.substring(4, 6), 16);
  return [r, g, b];
}

function getLuminance([r, g, b]) {
  const [rs, gs, bs] = [r, g, b].map((val) => {
    const c = val / 255;
    return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
  });
  return 0.2126 * rs + 0.7152 * gs + 0.0722 * bs;
}

function getContrastRatio(hex1, hex2) {
  const l1 = getLuminance(hexToRgb(hex1));
  const l2 = getLuminance(hexToRgb(hex2));
  const lighter = Math.max(l1, l2);
  const darker = Math.min(l1, l2);
  return (lighter + 0.05) / (darker + 0.05);
}

function runContrastAudit() {
  const checks = [];
  const themes = ['dark', 'light'];

  for (const theme of themes) {
    const pal = colors[theme];

    const pairs = [
      { fgName: 'text', bgName: 'bg', fg: pal.text, bg: pal.bg, min: 4.5 },
      { fgName: 'text', bgName: 'bgSubtle', fg: pal.text, bg: pal.bgSubtle, min: 4.5 },
      { fgName: 'text', bgName: 'surface', fg: pal.text, bg: pal.surface, min: 4.5 },
      { fgName: 'text', bgName: 'bgMuted', fg: pal.text, bg: pal.bgMuted, min: 4.5 },
      { fgName: 'textMuted', bgName: 'bg', fg: pal.textMuted, bg: pal.bg, min: 4.5 },
      { fgName: 'textMuted', bgName: 'surface', fg: pal.textMuted, bg: pal.surface, min: 4.5 },
      { fgName: 'primaryFg', bgName: 'primary', fg: pal.primaryFg, bg: pal.primary, min: 4.5 },
      { fgName: 'accentFg', bgName: 'accent', fg: pal.accentFg, bg: pal.accent, min: 4.5 },
      { fgName: 'accent', bgName: 'bg', fg: pal.accent, bg: pal.bg, min: 4.5 },
      { fgName: 'deal', bgName: 'bg', fg: pal.deal, bg: pal.bg, min: 4.5 },
      { fgName: 'warning', bgName: 'bg', fg: pal.warning, bg: pal.bg, min: 3.0 },
    ];

    for (const p of pairs) {
      const ratio = parseFloat(getContrastRatio(p.fg, p.bg).toFixed(2));
      checks.push({
        theme,
        foregroundName: p.fgName,
        backgroundName: p.bgName,
        fg: p.fg,
        bg: p.bg,
        ratio,
        minRequired: p.min,
        passes: ratio >= p.min,
      });
    }
  }

  const passed = checks.filter((c) => c.passes).length;
  return { total: checks.length, passed, results: checks };
}

// Execute
const audit = runContrastAudit();
console.log(`\n================ MECHWARE WCAG AA COLOR CONTRAST AUDIT ================`);
console.log(`Checked ${audit.total} semantic token pairs. Passed: ${audit.passed}/${audit.total}`);
console.log(`-----------------------------------------------------------------------`);
let allPass = true;
for (const r of audit.results) {
  const status = r.passes ? '✓ PASS' : '✗ FAIL';
  console.log(
    `[${r.theme.toUpperCase()}] ${r.foregroundName} (${r.fg}) on ${r.backgroundName} (${r.bg}): ${r.ratio}:1 (Required: ${r.minRequired}:1) -> ${status}`
  );
  if (!r.passes) allPass = false;
}
console.log(`=======================================================================\n`);

if (!allPass) {
  console.error('WCAG AA contrast audit failed!');
  process.exit(1);
} else {
  console.log('All token pairs pass WCAG AA with flying colors!');
}
