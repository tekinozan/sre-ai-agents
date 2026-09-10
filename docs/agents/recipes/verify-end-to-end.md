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
docker compose --project-name sre-e2e -f deploy/docker/compose.yaml up -d --build
```

PostgreSQL 17, Redpanda, Temporal with its own logical store, the OpenTelemetry
collector, Jaeger, a dedicated migration job, the API and both workers.
`Workflow__Engine` is `temporal`, so this exercises the durable path rather
than the inline development executor. The isolated project name keeps this
verification's resources separate from any other local Compose work.

The observability providers stay on the fakes here, deliberately: they produce
one coherent incident, and the assertions below are stable because of it. To
exercise the real Prometheus and Loki adapters instead, add the overlay:

```bash
docker compose --project-name sre-e2e -f deploy/docker/compose.yaml \
               -f deploy/docker/compose.observability.yaml up -d --build
```

Then seed them, or the investigation finds nothing:

```bash
bash eng/seed-observability.sh
powershell -ExecutionPolicy Bypass -File eng/seed-observability.ps1   # Windows
```

The seed reproduces the incident the fakes fabricate — a pool filling, clients
queueing behind it, errors following — in the real backends, so the report a real
run produces can be held next to the deterministic one and anything that differs
is the adapter's doing. Running the stack unseeded once is also worth it: "no
evidence" and "no answer" have to read differently in a report.

Evidence rows should then carry `provider = prometheus` rather than
`fake-prometheus`. `database.health` stays fake — that port has no real adapter
— and `logs.search` is not reached at all, because the scripted provider only
asks for it when an agent has no metrics tool and both shipped experts have one.

| What | Where |
|---|---|
| API | http://localhost:8080 |
| Metrics | http://localhost:8080/metrics |
| Jaeger | http://localhost:16686 |
| Temporal UI | http://localhost:8088 |
| PostgreSQL | `127.0.0.1:55432`, `businessowner` / `businessowner` |

## Verify PostgreSQL provisioning

On a fresh volume, the provisioner creates all three databases. The business and
Temporal owners are non-superusers; `sre_admin` is only the fresh-volume
bootstrap administrator.

```bash
docker compose --project-name sre-e2e -f deploy/docker/compose.yaml exec postgres \
  psql -U sre_admin -d postgres -c \
  "SELECT datname, pg_get_userbyid(datdba) AS owner FROM pg_database WHERE datname IN ('sre', 'temporal', 'temporal_visibility') ORDER BY datname;"

docker compose --project-name sre-e2e -f deploy/docker/compose.yaml exec postgres \
  psql -U sre_admin -d postgres -c \
  "SELECT rolname, rolsuper FROM pg_roles WHERE rolname IN ('businessowner', 'temporal') ORDER BY rolname;"

docker compose --project-name sre-e2e -f deploy/docker/compose.yaml run --rm postgres-provision
```

The second provision run must report that the roles and databases already exist
and leave credentials, attributes, and ownership unchanged.

For a retained legacy volume, validate against a cloned volume first before
touching primary volumes. Compose attaches `${project}_postgres-data`, so clone
the source volume directly into `sre-legacy_postgres-data`. The command mounts
the source read-only and refuses to overwrite an existing target:

```bash
docker volume create sre-legacy_postgres-data
docker run --rm \
  -v sre_agents_sre-postgres-data:/from:ro \
  -v sre-legacy_postgres-data:/to \
  alpine sh -c '
    if [ -n "$(ls -A /to 2>/dev/null)" ]; then
      echo "Target volume sre-legacy_postgres-data is already populated; aborting." >&2
      exit 1
    fi
    cp -a /from/. /to/
  '
```

Then set `POSTGRES_LEGACY_ADMIN_USER` and `POSTGRES_LEGACY_ADMIN_PASSWORD` to its
current `sre` administrator before starting the stack. If its `sre` database remains
owned by that role, also set `POSTGRES_BUSINESS_USER` and `POSTGRES_BUSINESS_PASSWORD`
to the existing owner for that run. This is intentional: the provisioner is additive
and will not transfer schema or database ownership.

When running alongside another local Compose stack, override host ports to avoid
port collisions: `SRE_API_PORT`, `SRE_POSTGRES_PORT`, `SRE_KAFKA_PORT`,
`SRE_TEMPORAL_PORT`, `SRE_TEMPORAL_UI_PORT`, `SRE_JAEGER_PORT`, `SRE_OTEL_PORT`.
Use `SRE_BIND_ADDRESS=0.0.0.0` only when an explicit non-loopback local binding is needed.

## Drive it

```bash
curl -X POST http://localhost:8080/v1/alerts/alertmanager \
  -H 'Content-Type: application/json' \
  -d '{"status":"firing","fingerprint":"verify-1",
       "labels":{"alertname":"HighCheckoutLatency","service":"checkout",
                 "environment":"production","severity":"critical","tenant":"local"},
       "annotations":{"summary":"Checkout p99 latency above 2s"}}'
```

Expect `202` with an **empty body**. This endpoint stages the alert onto the
topic; the worker opens the incident, so there is no id to return yet. Find it in
the database:

```bash
docker compose --project-name sre-e2e -f deploy/docker/compose.yaml exec postgres   psql -U businessowner -d sre -c "SELECT id, status FROM incidents ORDER BY created_at DESC LIMIT 1;"
```

The tenant must be `local`: development authentication issues that tenant and
every read filters by it, so an alert labelled otherwise produces an incident you
cannot read. A 404 there is tenant isolation working.

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
docker compose --project-name sre-e2e -f deploy/docker/compose.yaml stop investigation-worker
# send an alert
docker compose --project-name sre-e2e -f deploy/docker/compose.yaml start investigation-worker
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

## Stop the isolated stack

```bash
docker compose --project-name sre-e2e -f deploy/docker/compose.yaml down
```

This stops only the verification project and retains its named volume. The
provisioner is deliberately safe to repeat against that volume. Do not use
`down -v` for a retained or legacy database; remove a volume only through a
separate, deliberate local-data disposal procedure.

## What this does not cover

Interrupting an investigation *mid-flight* to exercise Temporal history replay.
The scripted provider finishes in about two seconds, so the race is not reliably
winnable by hand. Recovery across a restart is verified; replay of a partially
executed workflow is not.
