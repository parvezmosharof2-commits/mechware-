import test from 'node:test';
import assert from 'node:assert/strict';
import {
  assigneeIdProblem, assigneeText, canEditQuotes, canViewQuotes, closeQuoteHint, createQuoteBody, creditTermsLabel, deskErrorText, deskPageCount, deskQuery, deskReasonProblem,
  dhakaWhen, flagWords, hasCostFigures, isFinishedRfq, isOpenRfq, latestVersion, newQuoteProblems, quoteMoney, quoteStatusLabel, reviseHint, rfqCommandHint, rfqSla,
  rfqStatusLabel, shouldReloadDesk, staffLineCounts, staffLineHint, staffLineKind, staffLineLabel, versionsNewestFirst, workingTimeText,
  type StaffQuoteVersion, type StaffRfqSummary
} from '../src/quotes-desk-model.ts';

const ID = '0b9f1c52-6d3e-4c1a-9a55-2f7e8d1c4b60';
const rfq = (o: Partial<StaffRfqSummary> = {}): StaffRfqSummary => ({
  id: 'r1', number: 'RFQ-1', status: 'submitted', source: 'text', companyId: 'c1', companyName: 'Acme', requestedBy: 'u1', requesterName: 'Rina', assignedTo: null,
  assigneeName: null, lineCount: 2, neededBy: null, createdAt: '2026-11-22T03:00:00Z', firstQuoteSentAt: null, slaDueAt: '2026-11-22T11:00:00Z', slaWorkingMinutes: 90,
  slaBreached: false, closedAt: null, closeReason: null, withdrawnAt: null, ...o
});
const version = (no: number, o: Partial<StaffQuoteVersion> = {}): StaffQuoteVersion => ({ versionNo: no, id: `v${no}`, lines: [], ...o } as StaffQuoteVersion);

test('permissions: exact names, any case; read does not give write', () => {
  assert.equal(canViewQuotes(['QUOTES.VIEW']), true);
  assert.equal(canViewQuotes(['quotes.edit']), false);
  assert.equal(canEditQuotes(['quotes.view']), false);
  assert.equal(canEditQuotes(['quotes.view', 'quotes.edit']), true);
});

test('deskQuery: leaves out empty values, caps page size and q, drops a bad assigned value', () => {
  assert.equal(deskQuery(), 'page=1&pageSize=20');
  assert.equal(deskQuery({ status: 'OPEN', assigned: 'me', q: '  acme ' }), 'status=open&assigned=me&q=acme&page=1&pageSize=20');
  assert.equal(deskQuery({ assigned: 'none', page: 3, pageSize: 500 }), 'assigned=none&page=3&pageSize=50');
  assert.equal(deskQuery({ assigned: ID }), `assigned=${ID}&page=1&pageSize=20`);
  assert.equal(deskQuery({ assigned: 'someone' }), 'page=1&pageSize=20');
  assert.equal(deskQuery({ companyId: 'nope' }), 'page=1&pageSize=20');
  assert.equal(deskQuery({ companyId: ID, page: 0, pageSize: 0 }), `companyId=${ID}&page=1&pageSize=1`);
  assert.equal(new URLSearchParams(deskQuery({ q: 'x'.repeat(150) })).get('q')?.length, 100);
  assert.equal(new URLSearchParams(deskQuery({ q: 'a&b=c' })).get('q'), 'a&b=c');
});

test('deskPageCount', () => {
  assert.equal(deskPageCount(0, 20), 1);
  assert.equal(deskPageCount(41, 20), 3);
  assert.equal(deskPageCount(40, 20), 2);
});

test('status words', () => {
  assert.equal(rfqStatusLabel('quoting'), 'Being quoted');
  assert.equal(rfqStatusLabel('mystery_state'), 'mystery state');
  assert.equal(quoteStatusLabel('pending_approval'), 'Waiting for approval');
  assert.equal(creditTermsLabel('net30'), 'Net 30 days');
  assert.equal(isOpenRfq('submitted'), true);
  assert.equal(isOpenRfq('answered'), false);
  assert.equal(isFinishedRfq('withdrawn'), true);
  assert.equal(isFinishedRfq('quoting'), false);
  assert.equal(assigneeText('Ana', 'x'), 'Ana');
  assert.equal(assigneeText(null, null), 'Not assigned');
});

test('working time and the Dhaka clock', () => {
  assert.equal(workingTimeText(0), '0 min');
  assert.equal(workingTimeText(35), '35 min');
  assert.equal(workingTimeText(120), '2 h');
  assert.equal(workingTimeText(125), '2 h 05 min');
  assert.equal(workingTimeText(-1), '');
  assert.equal(dhakaWhen('2026-11-22T11:00:00Z'), '2026-11-22 17:00 Dhaka');
  assert.equal(dhakaWhen('2026-11-22T20:30:00'), '2026-11-23 02:30 Dhaka');
  assert.equal(dhakaWhen(null), '');
  assert.equal(dhakaWhen('garbage'), '');
});

test('SLA: breach is in words and follows the server verdict, not a recomputation', () => {
  const waiting = rfqSla(rfq());
  assert.equal(waiting.state, 'on_track');
  assert.equal(waiting.breached, false);
  assert.equal(waiting.label, 'Within SLA');
  assert.match(waiting.detail, /1 h 30 min of working time/);
  assert.match(waiting.detail, /due by 2026-11-22 17:00 Dhaka/);
  const late = rfqSla(rfq({ slaBreached: true, slaWorkingMinutes: 300 }));
  assert.equal(late.state, 'breached');
  assert.equal(late.label, 'SLA breached');
  assert.match(late.detail, /5 h of working time/);
  assert.equal(rfqSla(rfq({ status: 'answered', firstQuoteSentAt: '2026-11-22T05:00:00Z' })).state, 'answered_in_time');
  assert.equal(rfqSla(rfq({ status: 'answered', firstQuoteSentAt: '2026-11-22T05:00:00Z', slaBreached: true })).state, 'answered_late');
  assert.equal(rfqSla(rfq({ status: 'closed', closedAt: '2026-11-22T05:00:00Z' })).state, 'stopped_in_time');
  assert.equal(rfqSla(rfq({ status: 'withdrawn', slaBreached: true })).state, 'stopped_late');
  // a breached clock never reads as fine, and every state has words
  for (const s of [late, waiting, rfqSla(rfq({ status: 'closed', slaBreached: true }))]) assert.ok(s.label.length > 3 && s.detail.length > 10);
});

test('RFQ lines: kinds, labels, hints, counts', () => {
  assert.equal(staffLineKind('not_found'), 'notFound');
  assert.equal(staffLineKind('weird'), 'other');
  assert.equal(staffLineLabel('obsolete'), 'Obsolete part');
  assert.match(staffLineHint({ matchStatus: 'obsolete', successor: { id: 'v', sku: 's', title: 't', titleBn: 'b' } }), /replacement below/);
  assert.match(staffLineHint({ matchStatus: 'obsolete', successor: null }), /no replacement/);
  assert.match(staffLineHint({ matchStatus: 'not_found', successor: null }), /Not found/);
  assert.deepEqual(staffLineCounts([{ matchStatus: 'matched' }, { matchStatus: 'obsolete' }, { matchStatus: 'not_found' }, { matchStatus: 'ambiguous' }]), { matched: 1, obsolete: 1, unmatched: 2 });
});

test('command hints follow permissions first, then state', () => {
  assert.equal(rfqCommandHint(rfq(), ['quotes.view']).allowed, false);
  assert.match(rfqCommandHint(rfq(), ['quotes.view']).reason, /quotes\.edit/);
  assert.equal(rfqCommandHint(rfq(), ['quotes.edit']).allowed, true);
  assert.equal(rfqCommandHint(rfq({ status: 'closed' }), ['quotes.edit']).allowed, false);
  assert.equal(rfqCommandHint(rfq({ status: 'answered' }), ['quotes.edit']).allowed, true);
  assert.equal(reviseHint({ status: 'sent' }, ['quotes.edit']).allowed, true);
  assert.equal(reviseHint({ status: 'draft' }, ['quotes.edit']).allowed, false);
  assert.equal(reviseHint({ status: 'closed' }, ['quotes.edit']).allowed, false);
  assert.equal(reviseHint({ status: 'sent' }, ['quotes.view']).allowed, false);
  assert.equal(closeQuoteHint({ status: 'draft' }, ['quotes.edit']).allowed, true);
  assert.equal(closeQuoteHint({ status: 'closed' }, ['quotes.edit']).allowed, false);
  assert.equal(closeQuoteHint({ status: 'sent' }, []).allowed, false);
});

test('reason: 5 to 500 characters after trimming', () => {
  assert.equal(deskReasonProblem(''), 'Enter a reason.');
  assert.equal(deskReasonProblem('   '), 'Enter a reason.');
  assert.equal(deskReasonProblem(undefined), 'Enter a reason.');
  assert.match(deskReasonProblem(' abcd ') ?? '', /5 to 500/);
  assert.equal(deskReasonProblem(' abcde '), null);
  assert.equal(deskReasonProblem('x'.repeat(500)), null);
  assert.match(deskReasonProblem('x'.repeat(501)) ?? '', /5 to 500/);
});

test('assignee id and new-quote form', () => {
  assert.equal(assigneeIdProblem(ID), null);
  assert.ok(assigneeIdProblem(''));
  assert.ok(assigneeIdProblem('not-a-guid'));
  assert.deepEqual(newQuoteProblems({ companyId: ID, contactUserId: ` ${ID} ` }), {});
  assert.deepEqual(Object.keys(newQuoteProblems({ companyId: '', contactUserId: '' })), ['companyId', 'contactUserId']);
  assert.deepEqual(Object.keys(newQuoteProblems({ companyId: ID, contactUserId: 'x' })), ['contactUserId']);
  assert.deepEqual(createQuoteBody({ rfqId: 'r' }), { rfqId: 'r' });
  assert.deepEqual(createQuoteBody({ companyId: 'c', contactUserId: ' u ' }), { companyId: 'c', contactUserId: 'u' });
});

test('versions: latest by number whatever the order; cost column only when the API sent cost', () => {
  const vs = [version(2), version(3), version(1)];
  assert.equal(latestVersion(vs)?.versionNo, 3);
  assert.equal(latestVersion([]), null);
  assert.deepEqual(versionsNewestFirst(vs).map((v) => v.versionNo), [3, 2, 1]);
  assert.deepEqual(vs.map((v) => v.versionNo), [2, 3, 1]);
  assert.equal(hasCostFigures([{}, {}]), false);
  assert.equal(hasCostFigures([{ cost: undefined }, { cost: null }]), false);
  assert.equal(hasCostFigures([{}, { cost: { costMinorAtCheck: 1, floorMinorAtCheck: null } }]), true);
});

test('money and flags in words', () => {
  assert.equal(quoteMoney(123456), '1,234.56');
  assert.equal(quoteMoney(5), '0.05');
  assert.equal(quoteMoney(-250), '-2.50');
  assert.equal(quoteMoney(null), '');
  assert.deepEqual(flagWords(null), []);
  assert.deepEqual(flagWords({ belowFloorLineIds: [], overAuthorityLineIds: [], longValidity: false }), []);
  const nos = new Map([['a', 2], ['b', 1]]);
  assert.deepEqual(flagWords({ belowFloorLineIds: ['a', 'b'], overAuthorityLineIds: ['a'], longValidity: true }, nos), [
    'Below the margin floor on line 1, 2.', 'Beyond the preparer\'s price authority on line 2.', 'Valid for longer than the usual period.'
  ]);
  assert.match(flagWords({ belowFloorLineIds: ['zz'], overAuthorityLineIds: [], longValidity: false })[0], /1 line\(s\) below the margin floor/);
});

test('error words: the three desk codes are plain, the race codes reload', () => {
  const e = (o: Partial<{ status: number | null; code: string | null; message: string; fields: Record<string, string> }>) => ({ status: 409, code: null, message: '', fields: {}, ...o });
  assert.match(deskErrorText(e({ status: 403, code: 'QUOTE_CONFLICT_OF_INTEREST' })), /belong to this company/);
  assert.match(deskErrorText(e({ code: 'QUOTE_VERSION_CHANGED' })), /Reload/);
  assert.match(deskErrorText(e({ code: 'QUOTE_NUMBER_CLASH' })), /Try again/);
  assert.match(deskErrorText(e({ code: 'RFQ_INVALID_TRANSITION' })), /closed or withdrawn/);
  assert.equal(deskErrorText(e({ status: 400, fields: { reason: 'Give a reason of 5 to 500 characters.' } })), 'Give a reason of 5 to 500 characters.');
  assert.match(deskErrorText(e({ status: null })), /cannot be reached/);
  assert.match(deskErrorText(e({ status: 403 })), /permission/);
  assert.match(deskErrorText(e({ status: 404 })), /not found/);
  assert.match(deskErrorText(e({ status: 500 })), /server had a problem/);
  assert.equal(deskErrorText(e({ status: 418, message: 'Teapot' })), 'Teapot');
  assert.equal(shouldReloadDesk('QUOTE_VERSION_CHANGED'), true);
  assert.equal(shouldReloadDesk('QUOTE_NUMBER_CLASH'), false);
  assert.equal(shouldReloadDesk('QUOTE_CONFLICT_OF_INTEREST'), false);
  assert.equal(shouldReloadDesk(null), false);
});
