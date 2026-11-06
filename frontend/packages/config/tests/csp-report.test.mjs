import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { parseCspReports, createReportBudget, createCspReportHandler, MAX_BODY_BYTES } = require('../csp-report.js');
const { applyNonceCsp } = require('../security-headers.js');

const legacy = JSON.stringify({
  'csp-report': {
    'document-uri': 'https://mechware.com.bd/en/account',
    'violated-directive': 'script-src-elem',
    'effective-directive': 'script-src-elem',
    'blocked-uri': 'inline',
    'line-number': 12,
    'script-sample': 'alert(document.cookie)'
  }
});
const reportingApi = JSON.stringify([
  { type: 'csp-violation', url: 'https://mechware.com.bd/en', body: { documentURL: 'https://mechware.com.bd/en', effectiveDirective: 'script-src-attr', blockedURL: 'inline', lineNumber: 3 } },
  { type: 'deprecation', body: { id: 'x' } }
]);

test('both report formats are read', () => {
  assert.deepEqual(parseCspReports(legacy), [{
    documentUri: 'https://mechware.com.bd/en/account', directive: 'script-src-elem', blockedUri: 'inline', sourceFile: undefined, line: 12, sample: 'alert(document.cookie)'
  }]);
  const r = parseCspReports(reportingApi);
  assert.equal(r.length, 1, 'only csp-violation entries');
  assert.equal(r[0].directive, 'script-src-attr');
  assert.equal(r[0].line, 3);
});

test('anything else is ignored, not thrown', () => {
  // A real report made too long is refused too: the size limit, not the parser, stops it.
  const padded = legacy + ' '.repeat(MAX_BODY_BYTES);
  for (const body of ['', 'not json', '{}', '[1,2]', 'null', '{"csp-report": "x"}', padded]) {
    assert.deepEqual(parseCspReports(body), [], body.slice(0, 20));
  }
});

test('fields cannot forge log lines and are kept short', () => {
  const [v] = parseCspReports(JSON.stringify({ 'csp-report': { 'document-uri': 'https://a/\n{"event":"admin_login"}\r\u2028', 'script-sample': 'y'.repeat(5000), 'line-number': '7' } }));
  assert.ok(!/[\n\r\u2028]/.test(v.documentUri));
  assert.ok(v.sample.length <= 301);
  assert.equal(v.line, undefined, 'a line number must be an integer');
});

test('one body holds at most 10 reports', () => {
  const many = JSON.stringify(Array.from({ length: 50 }, () => ({ type: 'csp-violation', body: { effectiveDirective: 'script-src' } })));
  assert.equal(parseCspReports(many).length, 10);
});

test('the budget caps logged reports per minute and reports what it dropped', () => {
  let t = 0;
  const budget = createReportBudget(2, () => t);
  assert.equal(budget.take().allowed, true);
  assert.equal(budget.take().allowed, true);
  assert.equal(budget.take().allowed, false);
  assert.equal(budget.take().allowed, false);
  t = 60_000;
  assert.deepEqual(budget.take(), { allowed: true, droppedInLastWindow: 2 });
});

test('the handler logs cleaned reports, answers 204, and ignores oversized bodies', async () => {
  const lines = [];
  const POST = createCspReportHandler('web', { log: (l) => lines.push(l), budget: createReportBudget(1) });
  const req = (body, len = String(Buffer.byteLength(body))) =>
    new Request('http://localhost/api/csp-report', { method: 'POST', body, headers: { 'content-type': 'application/csp-report', 'content-length': len } });

  assert.equal((await POST(req(legacy))).status, 204);
  assert.equal(lines.length, 1);
  assert.deepEqual(JSON.parse(lines[0]).event, 'csp_violation');
  assert.equal(JSON.parse(lines[0]).app, 'web');

  assert.equal((await POST(req(legacy))).status, 204);
  assert.equal(lines.length, 1, 'over budget: dropped');

  // Fresh budget, so only the size limit can stop these.
  lines.length = 0;
  const POST2 = createCspReportHandler('web', { log: (l) => lines.push(l), budget: createReportBudget(100) });

  const big = legacy + ' '.repeat(MAX_BODY_BYTES);
  assert.equal((await POST2(req(big))).status, 204);
  assert.equal((await POST2(req(big, '10'))).status, 204, 'a lying content-length does not get past the limit');
  assert.equal(lines.length, 0);
  assert.equal((await POST2(req(legacy))).status, 204);
  assert.equal(lines.length, 1, 'a normal report still goes through');
});

test('an endless body is not read into memory', async () => {
  let pulled = 0;
  const endless = new ReadableStream({
    // "Endless" up to 4 MB, so a broken limit fails this test instead of exhausting memory.
    pull(controller) {
      if (pulled >= 4 * 1024 * 1024) return controller.close();
      pulled += 4096;
      controller.enqueue(new Uint8Array(4096).fill(32));
    }
  });
  const lines = [];
  const POST = createCspReportHandler('web', { log: (l) => lines.push(l) });
  const request = new Request('http://localhost/api/csp-report', { method: 'POST', body: endless, duplex: 'half', headers: { 'content-type': 'application/csp-report' } });
  assert.equal((await POST(request)).status, 204);
  assert.ok(pulled <= MAX_BODY_BYTES + 3 * 4096, `read ${pulled} bytes`);
  assert.equal(lines.length, 0);
});

test('dropped reports are counted in one line when the next minute starts', async () => {
  let t = 0;
  const lines = [];
  const POST = createCspReportHandler('console', { log: (l) => lines.push(JSON.parse(l)), budget: createReportBudget(1, () => t) });
  const req = () => new Request('http://localhost/api/csp-report', { method: 'POST', body: legacy });
  await POST(req());
  await POST(req());
  await POST(req());
  assert.deepEqual(lines.map((l) => l.event), ['csp_violation']);
  t = 60_000;
  await POST(req());
  assert.deepEqual(lines.slice(1), [{ event: 'csp_reports_dropped', app: 'console', count: 2 }, lines[2]]);
  assert.equal(lines[2].event, 'csp_violation');
});

test('the policy sends reports to the same app', () => {
  const csp = applyNonceCsp(new Headers(), { apiBaseUrl: 'https://api.mechware.com.bd', isDev: false });
  assert.match(csp, /report-uri \/api\/csp-report/);
  assert.doesNotMatch(csp, /report-to/, 'report-to would make Chrome stop using report-uri');
});
