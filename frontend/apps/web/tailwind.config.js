/** @type {import('tailwindcss').Config} */
module.exports = {
  presets: [require('@mechware/config/tailwind.preset.js')],
  content: [
    './src/**/*.{js,ts,jsx,tsx,mdx}',
    '../../packages/ui/**/*.{js,ts,jsx,tsx,mdx}'
  ]
};
