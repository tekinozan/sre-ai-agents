# Prove a change works

The gate proves a change compiles and that the unit tests still pass. It does not
prove the platform works. Roughly twenty defects here passed the gate and were
found only by running the process: an inbox row committed in its own transaction,
deduplication measured against the wrong clock, a sampler that hid every
investigation, a workflow that could never close.

Run this when your change touches anything that crosses a process boundary —
persistence, messaging, the workflow engine, serialization, telemetry,
configuration.

## Bring the stack up

```bash
docker compose -f deploy/docker/compose.yaml up -d --build
```

PostgreSQL 17, Redpanda, Temporal with its own store, the OpenTelemetry collector,
Jaeger, the API and both workers. `Workflow__Engine` is `temporal`, so this
exercises the durable path rather than the inline development executor.

| What | Where |
|---|---|
| API | http://localhost:8080 |
| Metrics | http://localhost:8080/metrics |
| Jaeger | http://localhost:16686 |
| Temporal UI | http://localhost:8088 |
| PostgreSQL | `localhost:55432`, `sre` / `sre` |

## Drive it

```bash
curl -X POST http://localhost:8080/v1/alerts/alertmanager \
  -H 'Content-Type: application/json' \
  -d '{"status":"firing","fingerprint":"verify-1",
       "labels":{"alertname":"HighCheckoutLatency","service":"checkout",
                 "environment":"production","severity":"critical","tenant":"local"},
       "annotations":{"summary":"Checkout p99 latency above 2s"}}'
```

Expect `202` and an incident id. The tenant must be `local`: development
authentication issues that tenant and every read filters by it, so an alert
labelled otherwise produces an incident you cannot read. A 404 there is tenant
isolation working.

```bash
curl "http://localhost:8080/v1/incidents/$ID"
curl "http://localhost:8080/v1/incidents/$ID/report"
curl "http://localhost:8080/v1/incidents/$ID/evidence"
curl "http://localhost:8080/v1/incidents/$ID/timeline"
curl -N "http://localhost:8080/v1/incidents/$ID/events"   # SSE, closes on terminal status
```

## What a healthy run looks like

Roughly ten seconds from alert to report.

- Status reaches `Resolved`; the report `outcome` is `Complete`
- Two agents contribute, every observation cites an evidence id that exists in
  `/evidence`, and the ranked hypotheses are ordered
- `agent_runs` shows non-zero `llm_call_count` and `tool_call_count` — zeroes here
  meant the counters were not persisted, which happened once
- One Jaeger trace spans all three services: `incident.investigation` →
  `classify` → `route` → `agent.execute` → `llm.call` → `tool.execute` →
  `correlate`. Not three separate traces
- No span carries a prompt, a token or a credential
- `GET /metrics` shows `sre_incident_started_total` incremented and
  `sre_trace_attribute_rejected_total` still `0`
- The Temporal workflow is `Completed`, not `Running`. A workflow still running
  against a resolved incident holds its id and blocks a retry

## The four checks worth doing every time

**Duplicate.** Send the same alert again. No second incident; a timeline entry is
added to the existing one. The deduplication window is measured from
`last_alert_at`, platform time, not the provider's `DetectedAt` (D23).

**Recovery.** Stop the investigation worker, send an alert, start it again. The
alert is still accepted, the work resumes, and exactly one report is produced.

```bash
docker compose -f deploy/docker/compose.yaml stop investigation-worker
# send an alert
docker compose -f deploy/docker/compose.yaml start investigation-worker
```

**Outbox drains.** `SELECT count(*) FROM outbox_messages WHERE dispatched_at IS
NULL;` settles at zero. A rising `attempt_count` means the dispatcher cannot reach
the broker; `is_dead_lettered` means it gave up.

**Nothing lost.** An inbox row is written in the same transaction as the effect,
so a row means the work committed. A dispatched outbox message with no
corresponding inbox row means a consumer is not running.

## When it does not work

`docs/RUNBOOK.md` diagnoses in pipeline order — API accepted it, outbox, inbox,
workflow started, what the investigation did — and names the table or topic that
proves each step ran. It also covers the specific failures this platform has
actually produced.

## Tear down

```bash
docker compose -f deploy/docker/compose.yaml down -v
```

`-v` drops the volumes. Leaving them keeps incidents from the last run, which
makes the next deduplication check lie to you.

## What this does not cover

Interrupting an investigation *mid-flight* to exercise Temporal history replay.
The scripted provider finishes in about two seconds, so the race is not reliably
winnable by hand. Recovery across a restart is verified; replay of a partially
executed workflow is not.
