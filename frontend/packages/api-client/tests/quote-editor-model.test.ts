import test from 'node:test';
import assert from 'node:assert/strict';
import {
  addLineProblem, buildDraftBody, contactLocked, discountWords, editableVersion, editorErrorText, editorFailureKind, editorProblems, formFromVersion, isDirty, lineNeedsReason, lineProblem,
  newLineFor, outcomeWords, poishaFromTaka, previewFlagWords, previewHasCost, previewKey, previewLineNotes, previewProblemText, referenceSourceText, removeLine,
  sortServerFields, takaFromPoisha, takaShown,
  type EditorForm, type EditorSourceVersion, type QuotePricePreview
} from '../src/quote-editor-model.ts';

const ID = '0b9f1c52-6d3e-4c1a-9a55-2f7e8d1c4b60';
const ID2 = '1c9f1c52-6d3e-4c1a-9a55-2f7e8d1c4b61';
const saved = (o: Partial<EditorSourceVersion> = {}): EditorSourceVersion => ({
  versionNo: 1, status: 'draft', rowVersion: 7, validityDays: 14, paymentTerms: 'as_account', shippingMode: 'calculated', shipToSiteId: null,
  termsText: null, buyerNote: null, internalNote: null,
  lines: [{ variantId: ID, sku: 'SKU-1', partNumber: 'PN-1', titleEn: 'Bearing', quantity: 3, unitPriceMinor: 125050, lineNote: null, belowFloorReason: null, rfqLineId: null }], ...o
});
const form = (o: Partial<EditorForm> = {}): EditorForm => ({ ...formFromVersion(saved(), ID2), ...o });

test('poishaFromTaka: string rule, no floats', () => {
  assert.equal(poishaFromTaka('12'), 1200);
  assert.equal(poishaFromTaka('12.5'), 1250);
  assert.equal(poishaFromTaka('1,234.05'), 123405);
  assert.equal(poishaFromTaka(' ৳ 0.10 '), 10);
  assert.equal(poishaFromTaka('19.99'), 1999);
  assert.equal(poishaFromTaka('1.005'), null);
  assert.equal(poishaFromTaka('-1'), null);
  assert.equal(poishaFromTaka('1e3'), null);
  assert.equal(poishaFromTaka(''), null);
  assert.equal(poishaFromTaka('abc'), null);
});

test('takaFromPoisha and takaShown round-trip without float error', () => {
  assert.equal(takaFromPoisha(125050), '1250.50');
  assert.equal(takaFromPoisha(5), '0.05');
  assert.equal(takaFromPoisha(null), '');
  for (const m of [1, 99, 100, 1999, 123456789]) assert.equal(poishaFromTaka(takaFromPoisha(m)), m);
  assert.equal(takaShown(123456789), '1,234,567.89');
});

test('formFromVersion: text as the form needs it; fixed delivery opens with no fees (the read has none)', () => {
  const f = formFromVersion(saved({ shippingMode: 'fixed', shipToSiteId: ID2, paymentTerms: 'advance', termsText: 'T' }), ID);
  assert.equal(f.lines[0].priceText, '1250.50');
  assert.equal(f.lines[0].quantity, '3');
  assert.equal(f.shippingMode, 'fixed');
  assert.equal(f.paymentTerms, 'advance');
  assert.deepEqual(f.fees, []);
  assert.equal(f.contactUserId, ID);
});

test('lineProblem: quantity, price and reason rules', () => {
  const l = newLineFor({ variantId: ID, sku: 'S', partNumber: null, title: 'T' });
  assert.match(lineProblem(l) ?? '', /net price/);
  assert.equal(lineProblem({ ...l, priceText: '10' }), null);
  assert.match(lineProblem({ ...l, priceText: '10', quantity: '0' }) ?? '', /at least 1/);
  assert.match(lineProblem({ ...l, priceText: '10', quantity: '1.5' }) ?? '', /whole number/);
  assert.match(lineProblem({ ...l, priceText: '10', quantity: '10001' }) ?? '', /10,000/);
  assert.match(lineProblem({ ...l, priceText: '0' }) ?? '', /0\.01/);
  assert.match(lineProblem({ ...l, priceText: '10', reason: 'abc' }) ?? '', /at least 5/);
  assert.equal(lineProblem({ ...l, priceText: '10', reason: 'abcde' }), null);
  assert.match(lineProblem({ ...l, priceText: '10', lineNote: 'x'.repeat(301) }) ?? '', /300/);
});

test('editorProblems: header, line numbers (1-based), fixed delivery needs site and one fee per seller id', () => {
  assert.equal(editorProblems(form()).count, 0);
  assert.ok(editorProblems(form({ validityDays: '91' })).header.validityDays);
  assert.ok(editorProblems(form({ validityDays: '0' })).header.validityDays);
  assert.ok(editorProblems(form({ validityDays: '1.5' })).header.validityDays);
  const f = form();
  f.lines.push(newLineFor({ variantId: ID2, sku: 'S2', partNumber: null, title: 'T2' }));
  assert.deepEqual(Object.keys(editorProblems(f).lines), ['2']);
  const fixed = editorProblems(form({ shippingMode: 'fixed' }));
  assert.ok(fixed.header.shipToSiteId && fixed.header.shippingFees);
  const fees = editorProblems(form({ shippingMode: 'fixed', shipToSiteId: ID, fees: [
    { key: 'a', sellerId: ID, label: '', feeText: '50' }, { key: 'b', sellerId: ID.toUpperCase(), label: '', feeText: '60' }, { key: 'c', sellerId: 'x', label: '', feeText: '1' }, { key: 'd', sellerId: ID2, label: '', feeText: '1.234' }
  ] }));
  assert.deepEqual(Object.keys(fees.fees), ['1', '2', '3']);
  assert.ok(editorProblems(form({ contactUserId: 'someone' })).header.contactUserId);
});

test('buildDraftBody: poisha, blanks left out, contact only when changed, calculated sends no site and no fees', () => {
  const b = buildDraftBody(form({ contactUserId: ID2 }), 7)!;
  assert.equal(b.rowVersion, 7);
  assert.equal(b.contactUserId, undefined);
  assert.deepEqual(b.lines, [{ variantId: ID, quantity: 3, unitPriceMinor: 125050 }]);
  assert.equal(b.shipToSiteId, undefined);
  assert.equal(b.shippingFees, undefined);
  assert.equal('termsText' in b, false);
  const withContact = buildDraftBody(form({ contactUserId: ID }))!;
  assert.equal(withContact.contactUserId, ID);
  assert.equal('rowVersion' in withContact, false);
  const f = form({ shippingMode: 'fixed', shipToSiteId: ` ${ID} `, fees: [{ key: 'a', sellerId: ID2, label: '', feeText: '120.5' }], termsText: ' Net ', buyerNote: 'hi', paymentTerms: 'advance' });
  f.lines[0].reason = ' below the floor because ';
  f.lines[0].lineNote = ' note ';
  f.lines[0].rfqLineId = ID2;
  const c = buildDraftBody(f, 3)!;
  assert.equal(c.shipToSiteId, ID);
  assert.deepEqual(c.shippingFees, [{ sellerId: ID2, shippingMinor: 12050 }]);
  assert.deepEqual(c.lines[0], { variantId: ID, quantity: 3, unitPriceMinor: 125050, lineNote: 'note', belowFloorReason: 'below the floor because', rfqLineId: ID2 });
  assert.equal(c.termsText, 'Net');
  assert.equal(c.paymentTerms, 'advance');
  assert.equal(buildDraftBody(form({ validityDays: '' })), null);
});

test('previewKey ignores the token; isDirty compares bodies', () => {
  const f = form();
  assert.equal(previewKey(buildDraftBody(f, 1)!), previewKey(buildDraftBody(f, 9)!));
  assert.equal(isDirty(f, form()), false);
  assert.equal(isDirty(form({ buyerNote: 'x' }), form()), true);
  const g = form();
  g.lines[0].quantity = '4';
  assert.equal(isDirty(g, f), true);
  assert.equal(isDirty(form({ validityDays: 'oops' }), form()), true);
});

test('addLineProblem: full list and duplicate item; removeLine by key', () => {
  const f = form();
  assert.match(addLineProblem(f.lines, ID) ?? '', /already on this quote/);
  assert.equal(addLineProblem(f.lines, ID2), null);
  const many = Array.from({ length: 50 }, (_, i) => newLineFor({ variantId: `v${i}`, sku: `S${i}`, partNumber: null, title: 't' }));
  assert.match(addLineProblem(many, ID2) ?? '', /at most 50/);
  assert.equal(removeLine(many, many[3].key).length, 49);
});

const preview = (o: Partial<QuotePricePreview> = {}): QuotePricePreview => ({
  versionNo: 1, outcome: 'approval', problems: [], belowFloorLineNos: [], overAuthorityLineNos: [], belowCostLineNos: [], reasonRequiredLineNos: [], longValidity: false,
  goodsNetMinor: 0, taxMinor: 0, goodsTotalMinor: 0, shippingTotalMinor: null, grandTotalMinor: null, lines: [], ...o
});

test('preview in words: outcome, problems, flags, per-line notes, reason required', () => {
  assert.equal(outcomeWords('send').tone, 'ok');
  assert.equal(outcomeWords('approval').tone, 'approval');
  assert.equal(outcomeWords('refused').tone, 'refused');
  assert.match(previewProblemText('SHIPPING_FEES_INCOMPLETE'), /every seller/);
  assert.match(previewProblemText('NO_LINES'), /no line/);
  assert.match(previewProblemText('QUOTE_BELOW_COST'), /below cost/);
  assert.match(previewProblemText('BELOW_FLOOR_REASON_REQUIRED'), /reason/);
  assert.equal(previewProblemText('SOMETHING_NEW'), 'something new');
  const p = preview({ belowFloorLineNos: [3, 1], overAuthorityLineNos: [2], belowCostLineNos: [4], longValidity: true, reasonRequiredLineNos: [1] });
  assert.deepEqual(previewFlagWords(p), ['Below cost on line 4.', 'Below the margin floor on line 1, 3.', "Deeper discount than the preparer's price authority allows on line 2.", 'Valid for more than 30 days.']);
  assert.deepEqual(previewLineNotes(p).get(1), ['Below the margin floor']);
  assert.equal(lineNeedsReason(p, 1), true);
  assert.equal(lineNeedsReason(p, 3), false);
  assert.equal(lineNeedsReason(null, 1), false);
  assert.deepEqual(previewFlagWords(preview()), []);
});

test('cost columns only when the API sent a cost object', () => {
  const line = { lineNo: 1 } as QuotePricePreview['lines'][number];
  assert.equal(previewHasCost(null), false);
  assert.equal(previewHasCost(preview({ lines: [line] })), false);
  assert.equal(previewHasCost(preview({ lines: [{ ...line, cost: { costMinorAtCheck: 1, floorMinorAtCheck: 2 } }] })), true);
});

test('referenceSourceText and discountWords', () => {
  assert.equal(referenceSourceText('quantity_break'), 'Quantity break');
  assert.equal(referenceSourceText('contract'), 'Contract price');
  assert.equal(referenceSourceText('weird_new'), 'Weird new');
  assert.equal(referenceSourceText(null), '');
  assert.equal(discountWords(80, 100), "20% below the buyer's price");
  assert.equal(discountWords(100, 100), '');
  assert.equal(discountWords(120, 100), '');
  assert.match(discountWords(9999, 10000), /Less than 1%/);
  assert.equal(discountWords(5, null), '');
});

test('errors: stale, not editable, contact locked, conflict of interest in plain words; kinds; server fields sorted', () => {
  const e = (status: number | null, code: string | null, fields: Record<string, string> = {}, message = '') => ({ status, code, message, fields });
  assert.match(editorErrorText(e(409, 'QUOTE_VERSION_CHANGED')), /edits are still on the screen/);
  assert.match(editorErrorText(e(409, 'QUOTE_NOT_EDITABLE')), /no longer a draft/);
  assert.match(editorErrorText(e(409, 'QUOTE_CONTACT_LOCKED')), /contact cannot be changed/);
  assert.match(editorErrorText(e(403, 'QUOTE_CONFLICT_OF_INTEREST')), /belong to this company/);
  assert.match(editorErrorText(e(400, null, { 'lines[2]': 'Quantity must be a multiple of 5.' })), /multiple of 5/);
  assert.match(editorErrorText(e(null, null)), /still on the screen/);
  assert.match(editorErrorText(e(403, null)), /permission/);
  assert.match(editorErrorText(e(500, null)), /still on the screen/);
  assert.equal(editorFailureKind('QUOTE_VERSION_CHANGED'), 'stale');
  assert.equal(editorFailureKind('QUOTE_NOT_EDITABLE'), 'gone');
  assert.equal(editorFailureKind('QUOTE_CLOSED'), 'gone');
  assert.equal(editorFailureKind(null), 'other');
  const s = sortServerFields({ 'lines[2]': 'MOQ 5', shippingFees: 'Fees', other: 'Odd', validityDays: 'Days' });
  assert.deepEqual(s.lines, { 2: 'MOQ 5' });
  assert.deepEqual(s.header, { shippingFees: 'Fees', validityDays: 'Days' });
  assert.deepEqual(s.general, ['Odd']);
});

test('editableVersion: only the latest version, only while it is a draft', () => {
  assert.equal(editableVersion([{ versionNo: 1, status: 'sent' }, { versionNo: 2, status: 'draft' }])?.versionNo, 2);
  assert.equal(editableVersion([{ versionNo: 1, status: 'draft' }, { versionNo: 2, status: 'sent' }]), null);
  assert.equal(editableVersion([]), null);
});

test('contactLocked: a version waiting, sent or accepted locks the contact; rejected and withdrawn do not', () => {
  assert.equal(contactLocked([{ status: 'draft' }, { status: 'sent' }]), true);
  assert.equal(contactLocked([{ status: 'pending_approval' }]), true);
  assert.equal(contactLocked([{ status: 'accepted' }]), true);
  assert.equal(contactLocked([{ status: 'draft' }, { status: 'rejected' }, { status: 'withdrawn' }, { status: 'superseded' }, { status: 'expired' }]), false);
});
