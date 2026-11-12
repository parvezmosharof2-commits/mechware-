import { FlatCompat } from '@eslint/eslintrc';
import { dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const compat = new FlatCompat({ baseDirectory: dirname(fileURLToPath(import.meta.url)) });

const config = [
  { ignores: ['.next/**', 'node_modules/**', 'next-env.d.ts', 'tailwind.config.js'] },
  ...compat.extends('next/core-web-vitals', 'next/typescript'),
  {
    // C7.8b: the barrel pulls every Radix component into first load (Lighthouse Perf fell below 90). Import components directly.
    files: ['src/**/*.{ts,tsx}'],
    ignores: ['src/app/[[]locale]/design-system/**'],
    rules: {
      'no-restricted-imports': ['error', {
        paths: [{ name: '@mechware/ui', message: "Import the component directly: '@mechware/ui/src/components/<Name>' (keeps first-load JS small)." }],
      }],
    },
  },
];

export default config;
