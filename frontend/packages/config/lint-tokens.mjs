import fs from 'node:fs';
import path from 'node:path';

const rootDir = path.resolve(import.meta.dirname, '../..');
const searchDirs = [
  path.resolve(rootDir, 'apps'),
  path.resolve(rootDir, 'packages'),
];

const arbitraryColorPattern = /\b(?:bg|text|border|ring|fill|stroke|from|to|via)-\[(?:#|rgb|hsl)[^\]]+\]/g;
const rawTailwindPalettePattern = /\b(?:bg|text|border|ring|fill|stroke|from|to|via)-(?:red|blue|green|yellow|purple|pink|gray|slate|zinc|neutral|stone|amber|sky|emerald|indigo|violet|rose|teal|cyan|orange|lime|fuchsia)-[1-9]00(?:\/\d+)?\b/g;

let violations = [];

function scanDir(dir) {
  if (!fs.existsSync(dir)) return;
  const entries = fs.readdirSync(dir, { withFileTypes: true });

  for (const entry of entries) {
    const fullPath = path.join(dir, entry.name);

    if (entry.isDirectory()) {
      if (
        entry.name === 'node_modules' ||
        entry.name === '.next' ||
        entry.name === 'dist' ||
        entry.name === 'bin' ||
        entry.name === 'obj'
      ) {
        continue;
      }
      scanDir(fullPath);
    } else if (
      entry.isFile() &&
      (entry.name.endsWith('.tsx') || entry.name.endsWith('.ts') || entry.name.endsWith('.jsx'))
    ) {
      // Exclude token definitions and test scripts themselves if needed, but let's check standard files
      const content = fs.readFileSync(fullPath, 'utf-8');
      const lines = content.split('\n');

      lines.forEach((line, lineNum) => {
        const arbitraryMatches = line.match(arbitraryColorPattern) || [];
        const rawMatches = line.match(rawTailwindPalettePattern) || [];
        const allMatches = [...arbitraryMatches, ...rawMatches];

        if (allMatches.length > 0) {
          violations.push({
            file: path.relative(rootDir, fullPath),
            line: lineNum + 1,
            matches: allMatches,
          });
        }
      });
    }
  }
}

for (const d of searchDirs) {
  scanDir(d);
}

console.log('\n================ MECHWARE TOKEN ENFORCEMENT LINTER ================');
if (violations.length > 0) {
  console.error(`✗ Detected ${violations.length} raw / arbitrary color usages:`);
  for (const v of violations) {
    console.error(`  - ${v.file}:${v.line} -> ${v.matches.join(', ')}`);
  }
  console.error('\nRule Violation: Raw Tailwind palette colors and arbitrary colors are strictly prohibited.');
  console.error('Please use defined semantic tokens (e.g. bg-accent, text-text, border-surface-border).');
  process.exit(1);
} else {
  console.log('✓ Token Lint Passed: Zero raw colors or arbitrary color classes detected!');
  console.log('All styling strictly complies with MECHWARE semantic design tokens.');
  console.log('===================================================================\n');
}
