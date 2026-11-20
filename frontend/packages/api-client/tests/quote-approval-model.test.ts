import test from 'node:test';
import assert from 'node:assert/strict';
import {
  acknowledgementProblem, approvalCommentProblem, approveHint, buildApproveBody, canApproveBelowFloor, canApproveQuotes, flagsFromMessage, issueErrorText,
  isStepUpRefusal, lineRefs, queueFlagWords, queueRows, recallHint, requiredLines, sendButtonState, sendFromDetailHint, sendOutcome, sendOutcomeTitle,
  shouldReloadAfterIssue, waitingText, withdrawHint,
  type QuoteApprovalItem, type QuoteSendResult, type Ticks
} from '../src/quote-approval-model.ts';

const A = '0b9f1c52-6d3e-4c1a-9a55-2f7e8d1c4b60';
const B = '1c9f1c52-6d3e-4c1a-9a55-2f7e8d1c4b61';
const C = '2d9f1c52-6d3e-4c1a-9a55-2f7e8d1c4b62';
const ME = '3e9f1c52-6d3e-4c1a-9a55-2f7e8d1c4b63';
const ticks = (below: string[] = [], over: string[] = []): Ticks => ({ belowFloor: new Set(below), overAuthority: new Set(over) });
const flagged = { flags: { belowFloorLineIds: [A], overAuthorityLineIds: [A, B], longValidity: false } };

test('sendButtonState: each reason, and the label follows the check', () => {
  const ok = { canEdit: true, dirty: false, lineCount: 2, valid: true, checking: false, outcome: 'send' as string | null };
  assert.deepEqual([sendButtonState(ok).allowed, sendButtonState(ok).label], [true, 'Send to the buyer']);
  assert.equal(sendButtonState({ ...ok, outcome: 'approval' }).label, 'Send for approval');
  assert.match(sendButtonState({ ...ok, outcome: 'approval' }).reason, /second person/);
  assert.equal(sendButtonState({ ...ok, outcome: null }).allowed, true);
  assert.match(sendButtonState({ ...ok, canEdit: false }).reason, /quotes\.edit/);
  assert.match(sendButtonState({ ...ok, lineCount: 0 }).reason, /at least one line/);
  assert.match(sendButtonState({ ...ok, valid: false }).reason, /marked fields/);
  assert.match(sendButtonState({ ...ok, dirty: true }).reason, /Save the draft first/);
  assert.match(sendButtonState({ ...ok, checking: true }).reason, /Wait/);
  assert.equal(sendButtonState({ ...ok, outcome: 'refused' }).allowed, false);
});

test('sendOutcome: reads the version that was sent or is waiting', () => {
  const v = (n: number, status: string) => ({ versionNo: n, status, validityDays: 14, sentAt: status === 'sent' ? '2026-11-20T10:00:00Z' : null, validUntil: status === 'sent' ? '2026-12-04T10:00:00Z' : null }) as never;
  const sent = { status: 'sent', flags: { belowFloorLineIds: [], overAuthorityLineIds: [], longValidity: false }, quote: { quote: {}, versions: [v(1, 'superseded'), v(2, 'sent')] } } as unknown as QuoteSendResult;
  const o = sendOutcome(sent);
  assert.deepEqual([o.kind, o.versionNo, o.validityDays, o.validUntil], ['sent', 2, 14, '2026-12-04T10:00:00Z']);
  assert.equal(sendOutcomeTitle(o), 'Version 2 was sent to the buyer.');
  const pend = sendOutcome({ ...sent, status: 'pending_approval', quote: { quote: {}, versions: [v(1, 'sent'), v(2, 'pending_approval')] } } as unknown as QuoteSendResult);
  assert.deepEqual([pend.kind, pend.versionNo, pend.validUntil], ['pending', 2, null]);
  assert.match(sendOutcomeTitle(pend), /waiting for approval/);
  assert.equal(sendOutcome({ ...sent, status: 'weird' }).kind, 'other');
});

test('requiredLines: the flags of the send, or what the server said now', () => {
  assert.deepEqual(requiredLines(flagged), { belowFloorLineIds: [A], overAuthorityLineIds: [A, B] });
  assert.deepEqual(requiredLines({ flags: null }), { belowFloorLineIds: [], overAuthorityLineIds: [] });
  assert.deepEqual(requiredLines(flagged, { belowFloorLineIds: [C], overAuthorityLineIds: [] }), { belowFloorLineIds: [C], overAuthorityLineIds: [] });
});

test('acknowledgementProblem: exactly the flagged lines, nothing more, nothing less', () => {
  const req = requiredLines(flagged);
  assert.equal(acknowledgementProblem(req, ticks([A], [A, B])), null);
  assert.match(acknowledgementProblem(req, ticks([A], [A])) ?? '', /1 of 3 still to tick/);
  assert.match(acknowledgementProblem(req, ticks([], [])) ?? '', /3 of 3/);
  assert.match(acknowledgementProblem(req, ticks([A, C], [A, B])) ?? '', /not flagged/);
  assert.match(acknowledgementProblem(req, ticks([A], [A, B, C])) ?? '', /not flagged/);
  assert.notEqual(acknowledgementProblem(req, ticks([B], [A, B])), null);
  assert.equal(acknowledgementProblem({ belowFloorLineIds: [], overAuthorityLineIds: [] }, ticks()), null);
  assert.equal(acknowledgementProblem({ belowFloorLineIds: [A], overAuthorityLineIds: [] }, ticks()), 'Tick the flagged line to show you have looked at it.');
});

test('buildApproveBody: the hash, exactly the flagged ids, a trimmed optional comment; null when anything is off', () => {
  const v = { contentHash: 'abc123' };
  const req = requiredLines(flagged);
  assert.deepEqual(buildApproveBody(v, req, ticks([A], [A, B]), '  Looked at the contract  '), { contentHash: 'abc123', belowFloorLineIds: [A], overAuthorityLineIds: [A, B], comment: 'Looked at the contract' });
  assert.equal('comment' in buildApproveBody(v, req, ticks([A], [A, B]), '   ')!, false);
  assert.equal(buildApproveBody(v, req, ticks([A], [A]), ''), null);
  assert.equal(buildApproveBody({ contentHash: null }, req, ticks([A], [A, B]), ''), null);
  assert.equal(buildApproveBody(v, req, ticks([A], [A, B]), 'x'.repeat(501)), null);
  assert.deepEqual(buildApproveBody(v, requiredLines({ flags: null }), ticks(), '')?.belowFloorLineIds, []);
});

test('approvalCommentProblem: optional on approval, 5-500 on refusal', () => {
  assert.equal(approvalCommentProblem('', false), null);
  assert.equal(approvalCommentProblem('   ', true)?.startsWith('Enter a comment'), true);
  assert.match(approvalCommentProblem('abcd', true) ?? '', /5 to 500/);
  assert.equal(approvalCommentProblem('abcde', true), null);
  assert.match(approvalCommentProblem('x'.repeat(501), false) ?? '', /at most 500/);
});

test('approveHint: author, submitter, contributor, permissions, below floor', () => {
  const base = { status: 'pending_approval', createdBy: A, contributors: [B], submittedBy: C, contentHash: 'h' };
  const approver = { id: ME, permissions: ['quotes.approve'] };
  const clean = requiredLines({ flags: null });
  assert.equal(approveHint(base, approver, clean).allowed, true);
  for (const who of [A, B, C]) assert.match(approveHint(base, { ...approver, id: who.toUpperCase() }, clean).reason, /created, changed or sent/);
  assert.match(approveHint({ ...base, status: 'sent' }, approver, clean).reason, /waiting for approval/);
  assert.match(approveHint(base, { id: ME, permissions: ['quotes.edit'] }, clean).reason, /quotes\.approve/);
  const below = requiredLines(flagged);
  assert.match(approveHint(base, approver, below).reason, /below-floor permission/);
  assert.equal(approveHint(base, { id: ME, permissions: ['quotes.approve', 'quotes.approve_below_floor'] }, below).allowed, true);
  assert.equal(approveHint({ ...base, contentHash: null }, approver, clean).allowed, false);
  assert.equal(canApproveQuotes(['QUOTES.APPROVE']), true);
  assert.equal(canApproveBelowFloor(['quotes.approve']), false);
});

test('recall, withdraw and send-from-detail hints', () => {
  assert.equal(recallHint({ status: 'pending_approval' }, ['quotes.edit']).allowed, true);
  assert.equal(recallHint({ status: 'sent' }, ['quotes.edit']).allowed, false);
  assert.equal(recallHint({ status: 'pending_approval' }, ['quotes.view']).allowed, false);
  assert.equal(withdrawHint({ status: 'sent' }, ['quotes.edit']).allowed, true);
  assert.match(withdrawHint({ status: 'accepted' }, ['quotes.edit']).reason, /sent to the buyer/);
  assert.equal(sendFromDetailHint({ status: 'draft', lines: [1] }, ['quotes.edit']).allowed, true);
  assert.match(sendFromDetailHint({ status: 'draft', lines: [] }, ['quotes.edit']).reason, /at least one line/);
  assert.equal(sendFromDetailHint({ status: 'sent', lines: [1] }, ['quotes.edit']).allowed, false);
});

test('queueRows: oldest first, flags as counts, the link, the advisory hint', () => {
  const item = (o: Partial<QuoteApprovalItem>): QuoteApprovalItem => ({
    quoteId: A, number: 'Q-1', versionNo: 1, companyId: B, companyName: 'Acme', submittedBy: C, submitterName: 'Support One', submittedAt: '2026-11-20T10:00:00Z', lineCount: 2,
    validityDays: 14, goodsNetMinor: 1000, goodsTotalMinor: 1150, grandTotalMinor: null, flags: { belowFloorLineIds: [], overAuthorityLineIds: [], longValidity: false }, canApprove: true, ...o
  });
  const rows = queueRows([item({ number: 'Q-2', submittedAt: '2026-11-20T12:00:00Z', canApprove: false, flags: { belowFloorLineIds: [A], overAuthorityLineIds: [A, B], longValidity: true } }), item({})]);
  assert.deepEqual(rows.map((r) => r.number), ['Q-1', 'Q-2']);
  assert.equal(rows[0].href, `/quotes/approvals/${A}/1`);
  assert.deepEqual(rows[0].flags, []);
  assert.deepEqual(rows[1].flags, ['1 line below the margin floor', "2 lines beyond the preparer's price authority", 'valid for longer than the usual period']);
  assert.match(rows[1].hint, /below-floor permission/);
  assert.equal(rows[0].hint, 'You can decide on this version.');
  assert.deepEqual(queueFlagWords(null), []);
});

test('waitingText', () => {
  const now = Date.parse('2026-11-20T12:00:00Z');
  assert.equal(waitingText('2026-11-20T11:59:00Z', now), 'just now');
  assert.equal(waitingText('2026-11-20T11:00:00Z', now), '60 minutes');
  assert.equal(waitingText('2026-11-20T06:00:00Z', now), '6 hours');
  assert.equal(waitingText('2026-11-17T12:00:00Z', now), '3 days');
  assert.equal(waitingText('garbage', now), '');
});

test('lineRefs: by line number, unknown ids still listed', () => {
  const lines = [{ id: A, lineNo: 2, sku: 'S2', titleEn: 'Two', unitPriceMinor: 500, belowFloorReason: 'match' }, { id: B, lineNo: 1, sku: 'S1', titleEn: 'One', unitPriceMinor: 900, belowFloorReason: null }] as never;
  const refs = lineRefs([A, C, B], lines);
  assert.deepEqual(refs.map((r) => r.lineNo), [1, 2, null]);
  assert.equal(refs[1].reason, 'match');
  assert.match(refs[2].title, /^Line /);
});

test('flagsFromMessage: the lines the server names now', () => {
  const msg = `Acknowledge exactly the lines flagged now. Below the floor: [${A}, ${B}]; over the discount authority: [${C}].`;
  assert.deepEqual(flagsFromMessage(msg), { belowFloorLineIds: [A, B], overAuthorityLineIds: [C] });
  assert.deepEqual(flagsFromMessage('Below the floor: []; over the discount authority: [].'), { belowFloorLineIds: [], overAuthorityLineIds: [] });
  assert.equal(flagsFromMessage('something else'), null);
  assert.equal(flagsFromMessage(null), null);
});

test('issueErrorText: every code in words, null for codes the desk words', () => {
  const e = (code: string | null, status = 409, fields: Record<string, string> = {}) => ({ status, code, message: '', fields });
  for (const c of ['STEP_UP_FAILED', 'STEP_UP_REQUIRED', 'QUOTE_FOUR_EYES', 'QUOTE_CHANGED', 'QUOTE_FLAGS_NOT_ACKNOWLEDGED', 'BELOW_FLOOR_PERMISSION_REQUIRED', 'BELOW_FLOOR_REASON_REQUIRED',
    'QUOTE_BELOW_COST', 'QUOTE_INVALID_TRANSITION', 'QUOTE_DELIVERY_SITE_GONE', 'COMPANY_NOT_ACTIVE', 'QUOTE_CONFLICT_OF_INTEREST', 'QUOTE_NOT_EDITABLE']) {
    assert.ok((issueErrorText(e(c)) ?? '').length > 20, c);
  }
  assert.match(issueErrorText(e('VALIDATION_ERROR', 400, { belowFloorReason: 'x' })) ?? '', /needs a reason/);
  assert.equal(issueErrorText(e('QUOTE_CLOSED')), null);
  assert.equal(issueErrorText(e(null, 500)), null);
  assert.equal(isStepUpRefusal('STEP_UP_FAILED'), true);
  assert.equal(isStepUpRefusal('QUOTE_FOUR_EYES'), false);
  for (const c of ['QUOTE_CHANGED', 'QUOTE_FLAGS_NOT_ACKNOWLEDGED', 'QUOTE_INVALID_TRANSITION']) assert.equal(shouldReloadAfterIssue(c), true);
  for (const c of ['QUOTE_FOUR_EYES', 'STEP_UP_FAILED', null]) assert.equal(shouldReloadAfterIssue(c), false);
});
