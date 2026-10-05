// Alert storm: the load the ingest path has to survive (production-readiness
// Phase 5, step 13).
//
// A storm is mostly repetition. Most deliveries are an alert that is already
// open firing again, which has to fold into its incident without a second
// investigation (docs/DECISIONS.md D43); a minority are new alerts that each
// open an incident and start one. FINGERPRINTS sets the size of that pool.
//
//   k6 run -e BASE_URL=http://localhost:8080 eng/load/alert-storm.js
//   k6 run -e BASE_URL=... -e TOKEN=<jwt> -e RAMP=5m -e SOAK=20m eng/load/alert-storm.js
//
// TOKEN is optional against the compose stack, whose development
// authentication issues the "local" tenant; against anything else it is
// required, and its tenant claim must match TENANT.
//
// Pass criteria are the thresholds below. Outside k6, watch that the outbox
// pending count stays flat and that no worker restarts or runs out of memory
// (docs/RUNBOOK.md, "Load and soak").

import http from 'k6/http';
import { check } from 'k6';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:8080';
const TOKEN = __ENV.TOKEN || '';
const TENANT = __ENV.TENANT || 'local';
const FINGERPRINTS = parseInt(__ENV.FINGERPRINTS || '50', 10);
const RUN = __ENV.RUN_ID || `${Date.now()}`;

export const options = {
  scenarios: {
    storm: {
      executor: 'ramping-arrival-rate',
      startRate: 1,
      timeUnit: '1s',
      preAllocatedVUs: 20,
      maxVUs: 100,
      stages: [
        { target: 10, duration: __ENV.RAMP || '5m' },
        { target: 10, duration: __ENV.SOAK || '20m' },
      ],
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<500'],
  },
};

export default function () {
  const fingerprint = `storm-${RUN}-${Math.floor(Math.random() * FINGERPRINTS)}`;
  const body = JSON.stringify({
    status: 'firing',
    alerts: [
      {
        status: 'firing',
        fingerprint,
        labels: {
          alertname: 'StormLatency',
          service: 'checkout',
          environment: 'production',
          severity: 'critical',
          tenant: TENANT,
        },
        annotations: { summary: 'Checkout p99 latency above 2s' },
      },
    ],
  });

  const headers = { 'Content-Type': 'application/json' };
  if (TOKEN) {
    headers.Authorization = `Bearer ${TOKEN}`;
  }

  const response = http.post(`${BASE_URL}/v1/alerts/alertmanager`, body, { headers });
  check(response, { 'accepted (202)': (r) => r.status === 202 });
}
