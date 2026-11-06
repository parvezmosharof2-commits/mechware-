/**
 * CSP violation reports (threat model M5 follow-up). The policy blocks injected script; these reports tell us that someone
 * tried, and from which page, so an XSS bug is found instead of silently blocked.
 *
 * Browsers post reports to the same app (`/api/csp-report`). The policy uses `report-uri` (`application/csp-report`,
 * `{ "csp-report": { "document-uri", "violated-directive", ... } }`); the Reporting API format
 * (`[{ "type": "csp-violation", "body": { "documentURL", ... } }]`) is read too, should `report-to` be added later.
 *
 * Everything in a report is attacker-controlled: it is size-limited, trimmed and stripped of control characters before it is
 * logged, and the number of reports logged per minute is capped so the endpoint cannot be used to flood the logs.
 */

const MAX_BODY_BYTES = 16 * 1024;
const MAX_FIELD = 300;
const MAX_REPORTS_PER_BODY = 10;

/** Trims a value to a short, single-line string (no control characters, so it cannot forge log lines). */
function clean(value) {
  if (value === undefined || value === null) return undefined;
  // eslint-disable-next-line no-control-regex
  const text = String(value).replace(/[\u0000-\u001f\u007f-\u009f\u2028\u2029]/g, ' ').trim();
  return text.length > MAX_FIELD ? `${text.slice(0, MAX_FIELD)}…` : text;
}

function fromLegacy(r) {
  return {
    documentUri: clean(r['document-uri']),
    directive: clean(r['effective-directive'] ?? r['violated-directive']),
    blockedUri: clean(r['blocked-uri']),
    sourceFile: clean(r['source-file']),
    line: Number.isInteger(r['line-number']) ? r['line-number'] : undefined,
    sample: clean(r['script-sample'])
  };
}

function fromReportingApi(r) {
  const b = r && typeof r.body === 'object' && r.body !== null ? r.body : {};
  return {
    documentUri: clean(b.documentURL ?? r.url),
    directive: clean(b.effectiveDirective),
    blockedUri: clean(b.blockedURL),
    sourceFile: clean(b.sourceFile),
    line: Number.isInteger(b.lineNumber) ? b.lineNumber : undefined,
    sample: clean(b.sample)
  };
}

/**
 * Parses a report body into at most 10 cleaned violations. Anything that is not a CSP report gives an empty list.
 *
 * @param {string} text the raw request body
 * @returns {Array<{ documentUri?: string, directive?: string, blockedUri?: string, sourceFile?: string, line?: number, sample?: string }>}
 */
function parseCspReports(text) {
  if (typeof text !== 'string' || text.length === 0 || text.length > MAX_BODY_BYTES) return [];
  let json;
  try {
    json = JSON.parse(text);
  } catch {
    return [];
  }
  if (Array.isArray(json)) {
    return json
      .filter((r) => r && typeof r === 'object' && r.type === 'csp-violation')
      .slice(0, MAX_REPORTS_PER_BODY)
      .map(fromReportingApi);
  }
  if (json && typeof json === 'object' && json['csp-report'] && typeof json['csp-report'] === 'object') {
    return [fromLegacy(json['csp-report'])];
  }
  return [];
}

/**
 * A fixed-window budget for how many reports are logged per minute (per server process). Further reports are dropped
 * and only counted, so a flood costs one log line per minute.
 *
 * @param {number} perMinute
 * @param {() => number} now
 */
function createReportBudget(perMinute = 60, now = Date.now) {
  let windowStart = now();
  let used = 0;
  let dropped = 0;
  return {
    /** @returns {{ allowed: boolean, droppedInLastWindow: number }} */
    take() {
      const t = now();
      let droppedInLastWindow = 0;
      if (t - windowStart >= 60_000) {
        droppedInLastWindow = dropped;
        windowStart = t;
        used = 0;
        dropped = 0;
      }
      if (used < perMinute) {
        used += 1;
        return { allowed: true, droppedInLastWindow };
      }
      dropped += 1;
      return { allowed: false, droppedInLastWindow };
    }
  };
}

/**
 * A Next.js route handler (`export const POST = createCspReportHandler('web')`). Always answers 204: the browser does not
 * care, and a different answer would only tell a prober something.
 *
 * @param {string} app name in the log line
 * @param {{ log?: (line: string) => void, budget?: ReturnType<typeof createReportBudget> }} options
 */
function createCspReportHandler(app, options = {}) {
  const log = options.log ?? ((line) => console.warn(line));
  const budget = options.budget ?? createReportBudget();
  return async function POST(request) {
    const declared = Number(request.headers.get('content-length') ?? '0');
    if (Number.isFinite(declared) && declared <= MAX_BODY_BYTES) {
      const text = await readLimited(request, MAX_BODY_BYTES);
      for (const violation of parseCspReports(text)) {
        const { allowed, droppedInLastWindow } = budget.take();
        if (droppedInLastWindow > 0) log(JSON.stringify({ event: 'csp_reports_dropped', app, count: droppedInLastWindow }));
        if (!allowed) break;
        log(JSON.stringify({ event: 'csp_violation', app, ...violation }));
      }
    }
    return new Response(null, { status: 204 });
  };
}

/** Reads at most `limit` bytes of the body; a longer body gives '' (it is not a report). */
async function readLimited(request, limit) {
  if (!request.body) return '';
  const reader = request.body.getReader();
  const chunks = [];
  let size = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    size += value.byteLength;
    if (size > limit) {
      await reader.cancel();
      return '';
    }
    chunks.push(value);
  }
  const all = new Uint8Array(size);
  let offset = 0;
  for (const c of chunks) {
    all.set(c, offset);
    offset += c.byteLength;
  }
  return new TextDecoder().decode(all);
}

module.exports = { parseCspReports, createReportBudget, createCspReportHandler, MAX_BODY_BYTES };
