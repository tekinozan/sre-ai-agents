# Runbook

Operating the platform, and diagnosing it when it is the thing that is broken.

Written for the case where an incident is not being investigated and somebody has
to find out why. The order below is roughly the order the pipeline runs in, so
working down it narrows the problem.

---

## Running it locally

```bash
docker compose -f deploy/docker/compose.yaml up -d --build
```

Brings up PostgreSQL 17 (hosting `sre`, `temporal`, `temporal_visibility`), Redpanda,
Temporal, the OpenTelemetry collector, Jaeger, DatabaseMigrator and all three application hosts.

| What | Where |
|---|---|
| API | http://localhost:8080 (override with `SRE_API_PORT`) |
| Metrics | http://localhost:8080/metrics |
| Jaeger | http://localhost:16686 (override with `SRE_JAEGER_PORT`) |
| Temporal UI | http://localhost:8088 (override with `SRE_TEMPORAL_UI_PORT`) |
| PostgreSQL | `127.0.0.1:55432`, `businessowner` / `businessowner` (override with `SRE_POSTGRES_PORT`) |

All published ports bind to `127.0.0.1` by default. Never tear down with `down -v` unless
permanently discarding local data; `docker compose down` retains the database volume safely.
Send an alert:

```bash
curl -X POST http://localhost:8080/v1/alerts/alertmanager \
  -H 'Content-Type: application/json' \
  -d '{"status":"firing","fingerprint":"demo-1",
       "labels":{"alertname":"HighCheckoutLatency","service":"checkout",
                 "environment":"production","severity":"critical","tenant":"local"},
       "annotations":{"summary":"Checkout p99 latency above 2s"}}'
```

Then read the result:

```bash
curl "http://localhost:8080/v1/incidents/$ID"
curl "http://localhost:8080/v1/incidents/$ID/report"
curl "http://localhost:8080/v1/incidents/$ID/evidence"
curl "http://localhost:8080/v1/incidents/$ID/timeline"
```

The tenant matters. Development authentication issues the tenant `local`, and
every read query filters by tenant, so an alert labelled `tenant: acme` produces
an incident the local principal cannot read. A 404 there is the isolation
working, not a bug.

---

## Checks

```bash
bash eng/ci.sh                      # the gate every commit passes
bash eng/ci.sh --with-integration   # adds the Testcontainers suites
```

The integration suites need a Docker daemon. They are excluded from the default
run so a machine without one is not blocked.

---

## An alert arrived and nothing happened

Work down the pipeline. Each step names the table or topic that proves it ran.

**1. Did the API accept it?** A `202` with an incident id means the alert was
staged. A `400` means the payload did not satisfy the adapter for that source;
the response carries the stable error code.

**2. Is it in the outbox?**

```sql
SELECT topic, event_type, dispatched_at, attempt_count, is_dead_lettered, last_error
FROM outbox_messages ORDER BY id DESC LIMIT 20;
```

Rows with `dispatched_at IS NULL` and a rising `attempt_count` mean the
dispatcher cannot reach the broker. `is_dead_lettered` means it gave up; the
payload is still there and can be replayed once the cause is fixed.

Nothing at all in the table means the write never happened — look at the API
logs for the transaction.

**3. Did a consumer take it?**

```sql
SELECT consumer_name, event_type, count(*) FROM inbox_messages GROUP BY 1, 2;
```

An inbox row is written in the same transaction as the effect, so a row means the
work committed. No row with a dispatched outbox message means the consumer is not
running or not reaching the broker.

**4. Did the workflow start?**

```bash
docker compose -f deploy/docker/compose.yaml exec temporal \
  temporal workflow list --address temporal:7233 --query "WorkflowType='SreInvestigation'"
```

The workflow id is `incident-{incidentId}`. `Running` long after the incident
closed means the workflow cannot complete — see below.

**5. What did the investigation do?**

```sql
SELECT agent_name, status, llm_call_count, tool_call_count, error_code FROM agent_runs;
SELECT tool_name, outcome, error_code FROM tool_calls ORDER BY executed_at DESC LIMIT 20;
SELECT event_type, summary FROM incident_events ORDER BY id;
```

The timeline is the operator-facing version of the same story, including why each
agent was excluded.

---

## Specific failures

### The report says no expert produced a verifiable finding

The agents ran and produced nothing usable. Check `agent_runs.error_code`:

- `LLM_STRUCTURED_OUTPUT_INVALID` — the model returned something the turn schema
  rejects. Compare the shape it returned against `AgentTurn`.
- `BUDGET_EXCEEDED` — the agent ran out of iterations, tokens or time. Expected
  under a tight budget; the outcome is `Partial` and the evidence collected so far
  is still cited.
- `TOOL_POLICY_DENIED` in `tool_calls` — the agent asked for something the policy
  release or its own allowlist forbids. That is the tool gateway working; the
  question is why the agent asked.

### Every observation is unverified

The finding cited evidence ids the platform never issued, and the binder dropped
them. Cross-check `evidence` against the ids in the finding. This is the defence
against a model inventing support for a conclusion, so it firing is not
necessarily wrong.

### A workflow stays Running after the incident closed

The workflow completed its work and then failed on the way out. Look at the tail
of its history:

```bash
docker compose -f deploy/docker/compose.yaml exec temporal \
  temporal workflow show --address temporal:7233 -w "incident-$ID"
```

`WORKFLOW_TASK_FAILED` with `NoMetadataForType` means a payload type is missing
from the generated serialization context. Add it to `WorkflowJsonContext` and
extend the round-trip test. A stuck workflow also holds its workflow id, so a
retry of that incident is refused as already running until it is terminated.

### The same alert opened two incidents

Deduplication is by `(tenant, source, fingerprint)`, and the database allows one
active incident per identity (`ix_incidents_active_fingerprint` is unique). A
recurrence also folds into a *resolved* incident whose `last_alert_at` is inside
the deduplication window: the report already explains that outage
(docs/DECISIONS.md D43). Two incidents therefore means either the fingerprints
differ or the alert was quiet for longer than the window between them. The usual
cause of the first is an identity label that is not stable — a pod name changes
on every restart, so a crash loop opens an incident per restart. Check
`IdentityLabelKeys` on the adapter for that source.

A resolved delivery from the source never opens an incident; it shows up as an
`alert.resolved` entry on the incident's timeline.

### A recurrence did not start a new investigation

Expected inside the cooldown of a resolved incident, and while an incident is
investigating or waiting on approval. A recurrence starts one only on an
incident that was never investigated, or on a failed one whose alert had been
quiet for a whole window (D43). Inside the window a failed incident stays
failed: retry it with `POST /v1/incidents/{id}/retry`.

### An alert is redelivered forever

Look for `7203` in the consumer logs. A handler failure leaves the offset
uncommitted and rewinds the partition to it after a short pause, so the message
returns on the next poll (docs/DECISIONS.md D42); `7204` means the rewind found
the partition revoked, and its new owner resumes from the committed offset.
After `Kafka:MaxDeliveryAttempts` attempts the message is dead-lettered (`7301`)
and the partition moves on.

A message for an alert that lost a creation race to another message is retried
the same way and folds into the winner's incident on the next attempt.

A historical instance of this: the creation claim outlived the incident it
created, so a recurrence could neither fold into a closed incident nor take the
claim. The claim is now thirty seconds (docs/DECISIONS.md D24).

### Investigations are missing from Jaeger

The investigation span is always sampled regardless of the ingress ratio: on
the Temporal engine it is `RunWorkflow:SreInvestigation`, inline it is
`incident.investigation`. If it is missing, either the OTLP endpoint is wrong or
the sampler was changed. Check `Telemetry:OtlpEndpoint`, then that
`InvestigationAwareSampler` is still the top-level sampler rather than wrapped in
a `ParentBasedSampler` — wrapping it means it is only consulted for root spans,
and an investigation never is one.

If `classify`, `route`, each `agent.execute` and `correlate` appear as separate
one-span traces, the Temporal interceptor's sources are not registered with the
tracer provider (`WorkflowEngineTraceSources`). If every `RunActivity:*` span
appears twice, a second `TracingInterceptor` was added to the worker; the
client's is already applied to it.

### `sre_trace_attribute_rejected_total` is climbing

Something is trying to put a non-allowlisted key on a span. The value is dropped,
so nothing leaked, but the attempt is a bug: either a new attribute needs adding
to `TraceAttributes` deliberately, or code is trying to record something section
16.1 forbids.

---

## Operating

### Promoting a configuration release

Edit the pinned releases in the ConfigMap and roll the deployments. Incidents
already open keep the snapshot they pinned at creation, so work in flight is
unaffected — that is what makes an old report explicable.

The kill switch is the documented exception: it is evaluated live and stops an
agent immediately, because it exists for the case where waiting for in-flight
work to drain is unacceptable.

### Schema migrations

`Database:MigrateOnStartup` is off in production. Several replicas racing for the
migration lock during a rolling update is a deployment failure that looks like a
flake, so the migration is a separate step in the pipeline where the SQL can be
reviewed before it runs.

Before deploying migrations against a new production database, configure split
runtime permissions using two explicit, single-authority bootstrap scripts:

1. **Database owner / admin** (`deploy/helm/sre-ai-agent/database-role-bootstrap-admin.sql`) grants CONNECT on the database to `sre_runtime`.
2. **Migration role** (`deploy/helm/sre-ai-agent/database-role-bootstrap.sql`) grants schema USAGE, table DML, and sequence access on existing objects, and sets ALTER DEFAULT PRIVILEGES for future migrations.

Neither script mutates role memberships, preserving existing administrator privileges and ADMIN OPTION grants. Both run with `ON_ERROR_STOP` inside transactions for atomic rollback on failure.
### Draining a worker

`SIGTERM`. The consumer stops taking new messages, finishes what it holds,
commits the offset and closes. The grace period is 60 seconds for the alert
worker and 120 for the investigation worker, which can be mid-model call. Killing
it sooner is safe — Temporal resumes the workflow — but the work is repeated
rather than continued, and the tokens already spent are spent again.

### Replaying a dead letter

The row keeps its payload. Reset `is_dead_lettered` and `next_attempt_at` once the
cause is fixed, and the dispatcher picks it up on its next pass. Consumers are
idempotent, so a replay of something that partly succeeded is safe.

### Running a pilot against a real provider and a real target

`deploy/docker/compose.tickethub-pilot.yaml` is a fourth compose layer, on top
of `compose.yaml` + `compose.observability.yaml`, that points `Llm:Bootstrap`
at a real model provider instead of Scripted and redirects observability to a
pilot target's own Prometheus/Loki. Adapter kind, endpoint, per-profile model
and the credential variable name are all compose substitution parameters —
read the file's own header for the full parameter list, the capability
mismatch a non-OpenAI-shaped provider hits today, and the non-destructive
sequence (management API, not `docker compose down -v`) for changing the
provider on a tenant that has already bootstrapped.
