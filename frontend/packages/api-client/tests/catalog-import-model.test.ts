import test from 'node:test';
import assert from 'node:assert/strict';
import { IMPORT_MAX_BYTES, checkImportFile, csvCell, errorsToCsv, isGuid } from '../src/catalog-import-model.ts';

test('client-side file checks: csv only, not empty, at most 10 MB', () => {
  assert.equal(checkImportFile(null), 'file.missing');
  assert.equal(checkImportFile({ name: 'a.xlsx', size: 10 }), 'file.notCsv');
  assert.equal(checkImportFile({ name: 'a.csv.exe', size: 10 }), 'file.notCsv');
  assert.equal(checkImportFile({ name: 'a.csv', size: 0 }), 'file.empty');
  assert.equal(checkImportFile({ name: 'A.CSV', size: IMPORT_MAX_BYTES }), null);
  assert.equal(checkImportFile({ name: 'a.csv', size: IMPORT_MAX_BYTES + 1 }), 'file.tooLarge');
});

test('seller id must be a GUID', () => {
  assert.equal(isGuid(' 3f2504e0-4f89-41d3-9a0c-0305e82c3301 '), true);
  assert.equal(isGuid('not-a-guid'), false);
  assert.equal(isGuid(''), false);
});

test('csv cells: formula starts get a quote, special characters are quoted', () => {
  for (const s of ['=1+1', '+1', '-1', '@x', '\tx', '\rx']) assert.ok(csvCell(s).replace(/^"/, '').startsWith("'"), s);
  assert.equal(csvCell('plain'), 'plain');
  assert.equal(csvCell('a,b'), '"a,b"');
  assert.equal(csvCell('say "hi"'), '"say ""hi"""');
  assert.equal(csvCell(null), '');
  assert.equal(csvCell(7), '7');
  assert.equal(csvCell('=HYPERLINK("x")'), `"'=HYPERLINK(""x"")"`);
});

test('error csv: header, null column, escaping', () => {
  const csv = errorsToCsv([
    { row: 0, column: null, code: 'MISSING_COLUMN', message: 'Missing "sku"' },
    { row: 3, column: 'sku', code: 'FORMULA_INJECTION', message: '=cmd' }
  ]);
  assert.equal(csv, 'row,column,code,message\r\n0,,MISSING_COLUMN,"Missing ""sku"""\r\n3,sku,FORMULA_INJECTION,\'=cmd\r\n');
});
