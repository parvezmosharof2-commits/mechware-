import test from 'node:test';
import assert from 'node:assert/strict';
import { parseDashboardTab } from './dashboard-tab.ts';

test('known tabs open as given', () => {
  for (const tab of ['sites', 'costCentres', 'approvals', 'members'] as const) assert.equal(parseDashboardTab(tab), tab);
});

test('missing, empty, unknown or differently cased values open Sites', () => {
  for (const v of [undefined, '', 'Approvals', 'billing', 'approvals ', '__proto__', 'toString']) assert.equal(parseDashboardTab(v), 'sites');
});

test('a repeated parameter uses the first value', () => {
  assert.equal(parseDashboardTab(['approvals', 'members']), 'approvals');
  assert.equal(parseDashboardTab([]), 'sites');
});
