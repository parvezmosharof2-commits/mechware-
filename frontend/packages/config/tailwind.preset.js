/** @type {import('tailwindcss').Config} */
module.exports = {
  darkMode: 'class',
  theme: {
    extend: {
      colors: {
        bg: {
          DEFAULT: 'var(--bg, #060B18)',
          subtle: 'var(--bg-subtle, #0A1128)',
          muted: 'var(--bg-muted, #101B3B)'
        },
        surface: {
          DEFAULT: 'var(--surface, #0D1635)',
          border: 'var(--border, #1B2B55)',
          'border-strong': 'var(--border-strong, #2A4078)'
        },
        text: {
          DEFAULT: 'var(--text, #F8FAFC)',
          muted: 'var(--text-muted, #94A3B8)',
          subtle: 'var(--text-subtle, #64748B)'
        },
        accent: {
          DEFAULT: 'var(--accent, #00E5FF)',
          fg: 'var(--accent-fg, #060B18)',
          hover: 'var(--accent-hover, #38EFFF)',
          teal: '#00A8B5'
        },
        brand: {
          navy: '#0F3E7D',
          blue: '#1A5CB0'
        },
        status: {
          online: '#34D399',
          fault: '#F87171',
          pending: '#FBBF24'
        }
      },
      fontFamily: {
        sans: ['var(--font-inter)', 'Inter', 'sans-serif'],
        display: ['var(--font-jakarta)', 'Plus Jakarta Sans', 'sans-serif'],
        bengali: ['var(--font-hind)', 'Hind Siliguri', 'sans-serif']
      },
      borderRadius: {
        xs: '4px',
        sm: '6px',
        md: '10px',
        lg: '14px',
        xl: '20px'
      }
    }
  }
};
