/** @type {import('tailwindcss').Config} */
module.exports = {
  darkMode: 'class',
  theme: {
    extend: {
      colors: {
        bg: {
          // Aliases used by the auth / account screens (kept so one vocabulary maps onto the semantic tokens below).
          canvas: 'var(--bg, #060B18)',
          surface: 'var(--surface, #0D1635)',
          elevated: 'var(--bg-muted, #101B3B)',
          inverse: '#060B18',
          DEFAULT: 'var(--bg, #060B18)',
          subtle: 'var(--bg-subtle, #0A1128)',
          muted: 'var(--bg-muted, #101B3B)',
        },
        surface: {
          DEFAULT: 'var(--surface, #0D1635)',
          border: 'var(--border, #1B2B55)',
          'border-strong': 'var(--border-strong, #2A4078)',
        },
        border: {
          subtle: 'var(--border, #1B2B55)',
          DEFAULT: 'var(--border, #1B2B55)',
          strong: 'var(--border-strong, #2A4078)',
        },
        text: {
          DEFAULT: 'var(--text, #F8FAFC)',
          muted: 'var(--text-muted, #94A3B8)',
          subtle: 'var(--text-subtle, #64748B)',
        },
        primary: {
          DEFAULT: 'var(--primary, #F8FAFC)',
          fg: 'var(--primary-fg, #060B18)',
        },
        accent: {
          DEFAULT: 'var(--accent, #00E5FF)',
          fg: 'var(--accent-fg, #060B18)',
          hover: 'var(--accent-hover, #38EFFF)',
          teal: '#00A8B5',
        },
        brand: {
          primary: 'var(--accent, #00E5FF)',
          'primary-hover': 'var(--accent-hover, #38EFFF)',
          accent: 'var(--accent, #00E5FF)',
          navy: 'var(--brand-navy, #0F3E7D)',
          blue: 'var(--brand-blue, #1A5CB0)',
        },
        fg: {
          primary: 'var(--text, #F8FAFC)',
          secondary: 'var(--text-muted, #94A3B8)',
          muted: 'var(--text-muted, #94A3B8)',
          'on-accent': 'var(--accent-fg, #060B18)',
        },
        state: {
          danger: 'var(--fault, #F87171)',
          success: 'var(--success, #34D399)',
        },
        deal: 'var(--deal, #F87171)',
        fault: 'var(--fault, #F87171)',
        success: 'var(--success, #34D399)',
        online: 'var(--online, #34D399)',
        warning: 'var(--warning, #FBBF24)',
        pending: 'var(--pending, #FBBF24)',
        status: {
          online: 'var(--status-online, #34D399)',
          success: 'var(--status-online, #34D399)',
          pending: 'var(--status-pending, #FBBF24)',
          warning: 'var(--status-pending, #FBBF24)',
          fault: 'var(--status-fault, #F87171)',
        },
        'focus-ring': 'var(--focus-ring, #00E5FF)',
      },
      boxShadow: {
        xs: '0 1px 2px rgba(15, 23, 42, 0.05)',
        sm: '0 1px 3px rgba(15, 23, 42, 0.08), 0 1px 2px rgba(15, 23, 42, 0.04)',
        md: '0 4px 12px rgba(15, 23, 42, 0.08)',
        lg: '0 12px 32px rgba(15, 23, 42, 0.12)',
        xl: '0 24px 64px rgba(15, 23, 42, 0.16)',
        'glow-cyan': '0 0 20px rgba(0, 229, 255, 0.35)',
        'glow-cyan-sm': '0 0 10px rgba(0, 229, 255, 0.25)',
        'glow-fault': '0 0 20px rgba(248, 113, 113, 0.35)',
      },
      fontFamily: {
        sans: ['var(--font-inter)', 'Inter', '-apple-system', 'BlinkMacSystemFont', 'sans-serif'],
        display: ['var(--font-jakarta)', 'Plus Jakarta Sans', 'sans-serif'],
        bengali: ['var(--font-hind)', 'Hind Siliguri', 'sans-serif'],
        mono: ['ui-monospace', 'SFMono-Regular', 'Menlo', 'Monaco', 'Consolas', 'monospace'],
      },
      borderRadius: {
        xs: '4px',
        sm: '6px',
        md: '10px',
        lg: '14px',
        xl: '20px',
        full: '9999px',
      },
      zIndex: {
        base: '0',
        sticky: '100',
        header: '200',
        dropdown: '300',
        drawer: '400',
        modal: '500',
        toast: '600',
        tooltip: '700',
      },
      maxWidth: {
        container: '1440px',
      },
      transitionDuration: {
        fast: '120ms',
        base: '200ms',
        slow: '320ms',
      },
    },
  },
};
