// Storefront search load test (C6.5). Run:
//   docker run --rm -i -e BASE_URL=http://host.docker.internal:5000 grafana/k6:0.54.0 run - < infra/k6/search.js
import http from 'k6/http';
import { check } from 'k6';

const BASE = __ENV.BASE_URL || 'http://host.docker.internal:5000';
const WORDS = ['inverter', 'motor', 'sensor', 'contactor', 'siemens', 'abb', 'schneider', 'plc', 'cable', 'breaker'];
const PREFIXES = ['in', 'mot', 'sen', 'con', 'sie', 'abb', 'sch', 'plc', 'cab', 'bre', 'vf', 'ser'];

export const options = {
  summaryTrendStats: ['avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
  stages: [
    { duration: '30s', target: 20 },
    { duration: '60s', target: 20 },
    { duration: '10s', target: 0 },
  ],
  thresholds: {
    http_req_duration: ['p(95)<150'],
    http_req_failed: ['rate<0.01'],
  },
};

const pick = (list) => list[Math.floor(Math.random() * list.length)];

// Real part numbers from the running catalogue, so part-number queries hit the exact-key path.
export function setup() {
  const res = http.get(`${BASE}/api/v1/search?q=siemens&pageSize=48`);
  const parts = [];
  if (res.status === 200) {
    for (const hit of res.json('data')) {
      if (hit.partNumbers && hit.partNumbers.length > 0) parts.push(hit.partNumbers[0]);
    }
  }
  if (parts.length === 0) throw new Error(`no part numbers found at ${BASE}; is the catalogue seeded and indexed?`);
  return { parts };
}

export default function (data) {
  const roll = Math.random();
  let url;
  if (roll < 0.5) url = `${BASE}/api/v1/search?q=${pick(WORDS)}`;
  else if (roll < 0.8) url = `${BASE}/api/v1/search?q=${encodeURIComponent(pick(data.parts))}`;
  else url = `${BASE}/api/v1/search/autocomplete?q=${pick(PREFIXES)}`;
  const res = http.get(url);
  check(res, { 'status 200': (r) => r.status === 200 });
}
