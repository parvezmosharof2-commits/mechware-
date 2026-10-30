#!/usr/bin/env node
/**
 * Fails when a route's first-load JavaScript (gzip) is over budget. Run after `next build`.
 * Brief v2 section 11: initial route JS < 170 KB gzip. Sizes use 1 KB = 1000 bytes, like `next build` does.
 *
 *   node packages/config/check-bundle-budget.mjs [appDir ...]      (default: apps/web apps/console)
 *
 * Exempt routes are demo or technical pages, not customer journeys.
 */
import { existsSync, readFileSync } from 'node:fs';
import { gzipSync } from 'node:zlib';
import { join, resolve } from 'node:path';

const BUDGET_KB = Number(process.env.BUNDLE_BUDGET_KB ?? 170);
const EXEMPT = [/\/design-system\/page$/, /^\/_not-found\/page$/, /^\/_global-error\/page$/];
// Layouts are not routes: their chunks are already counted inside every page that uses them.
const isLayout = (route) => route.endsWith('/layout');

const apps = process.argv.slice(2);
if (apps.length === 0) apps.push('apps/web', 'apps/console');

let failed = false;
for (const app of apps) {
  const dir = resolve(app, '.next');
  const manifestPath = join(dir, 'app-build-manifest.json');
  if (!existsSync(manifestPath)) {
    console.error(`${app}: no build found (run next build first)`);
    failed = true;
    continue;
  }
  const pages = JSON.parse(readFileSync(manifestPath, 'utf8')).pages;
  const cache = new Map();
  const gzKb = (file) => {
    if (!cache.has(file)) cache.set(file, gzipSync(readFileSync(join(dir, file))).length);
    return cache.get(file);
  };

  const rows = Object.entries(pages)
    .filter(([route]) => !isLayout(route))
    .map(([route, files]) => {
      const js = [...new Set(files)].filter((f) => f.endsWith('.js'));
      return { route, kb: js.reduce((sum, f) => sum + gzKb(f), 0) / 1000 };
    })
    .sort((a, b) => b.kb - a.kb);

  console.log(`\n${app}  (budget ${BUDGET_KB} kB gzip first-load JS)`);
  for (const { route, kb } of rows) {
    const exempt = EXEMPT.some((re) => re.test(route));
    const over = kb > BUDGET_KB && !exempt;
    if (over) failed = true;
    console.log(`  ${over ? 'OVER ' : exempt ? 'skip ' : 'ok   '} ${kb.toFixed(1).padStart(6)} kB  ${route}`);
  }
}

if (failed) {
  console.error(`\nBundle budget exceeded (or a build is missing). Lazy-load the heavy part (next/dynamic) or import less.`);
  process.exit(1);
}
console.log('\nAll routes are within the bundle budget.');
