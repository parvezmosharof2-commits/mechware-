import test from 'node:test';
import assert from 'node:assert/strict';
import {
  TERMS_MAX_CHARS, TERMS_MAX_LINES, canAddTerm, canChangeTerms, formFromTerms, moveTerm, newTermLine, removeTerm, setTermText,
  termCounter, termLineProblem, termsBody, termsDirty, termsErrorText, termsFailureKind, termsProblems
} from '../src/quote-terms-model.ts';

const form = () => formFromTerms({ termsEn: ['A', 'B', 'C'], termsBn: ['ক'] });

test('line limits: empty, 300 ok, 301 refused, counted after trimming', () => {
  assert.ok(termLineProblem('   '));
  assert.equal(termLineProblem('x'.repeat(TERMS_MAX_CHARS)), null);
  assert.match(termLineProblem('x'.repeat(TERMS_MAX_CHARS + 1))!, /1 characters too long/);
  assert.equal(termLineProblem(` ${'x'.repeat(TERMS_MAX_CHARS)} `), null);
  assert.equal(termCounter(' ab '), '2 / 300');
});

test('at most 12 lines', () => {
  const lines = Array.from({ length: TERMS_MAX_LINES }, () => newTermLine('x'));
  assert.equal(canAddTerm(lines), false);
  assert.equal(canAddTerm(lines.slice(1)), true);
});

test('move, remove and edit keep the other lines; moving past an end changes nothing', () => {
  const f = form();
  assert.deepEqual(moveTerm(f.en, 0, 1).map((l) => l.text), ['B', 'A', 'C']);
  assert.deepEqual(moveTerm(f.en, 2, 1).map((l) => l.text), ['A', 'B', 'C']);
  assert.deepEqual(moveTerm(f.en, 0, -1).map((l) => l.text), ['A', 'B', 'C']);
  assert.deepEqual(removeTerm(f.en, 1).map((l) => l.text), ['A', 'C']);
  assert.deepEqual(setTermText(f.en, 2, 'Z').map((l) => l.text), ['A', 'B', 'Z']);
});

test('dirty compares trimmed text and order', () => {
  const base = form();
  assert.equal(termsDirty(form(), base), false);
  assert.equal(termsDirty({ ...base, en: setTermText(base.en, 0, ' A ') }, base), false);
  assert.equal(termsDirty({ ...base, en: moveTerm(base.en, 0, 1) }, base), true);
  assert.equal(termsDirty({ ...base, bn: [] }, base), true);
});

test('body is trimmed with the row version, null while a line is bad; both lists may be empty', () => {
  const f = form();
  assert.deepEqual(termsBody({ ...f, en: setTermText(f.en, 0, ' A ') }, 7), { termsEn: ['A', 'B', 'C'], termsBn: ['ক'], rowVersion: 7 });
  assert.equal(termsBody({ ...f, bn: [newTermLine('')] }, 7), null);
  assert.deepEqual(termsBody({ en: [], bn: [] }, 1), { termsEn: [], termsBn: [], rowVersion: 1 });
  assert.match(termsProblems({ ...f, bn: [newTermLine('')] }).first!, /^Bangla line 1/);
  assert.equal(termsProblems(f).count, 0);
});

test('permission, failure kind and words', () => {
  assert.equal(canChangeTerms(['quotes.view']), false);
  assert.equal(canChangeTerms(['Quotes.Approve']), true);
  assert.equal(termsFailureKind('QUOTE_TERMS_CHANGED'), 'stale');
  assert.equal(termsFailureKind(null), 'other');
  const e = { status: 409, code: 'QUOTE_TERMS_CHANGED', message: '', fields: {} };
  assert.match(termsErrorText(e), /Someone else changed/);
  assert.match(termsErrorText({ ...e, status: 403, code: null }), /quotes\.approve/);
  assert.match(termsErrorText({ ...e, status: null, code: null }), /cannot be reached/);
  assert.equal(termsErrorText({ status: 400, code: null, message: '', fields: { a: 'Bad.' } }), 'Bad.');
});
