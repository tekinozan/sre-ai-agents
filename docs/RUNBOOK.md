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
- `BUDGET_EXCEEDED` — the agent ran out of iterations, tokens or time, or the
  tenant's model budget is spent (below). Expected under a tight budget; the
  report outcome is `BudgetExceeded` and the evidence collected so far is still
  cited.
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

### A report's outcome is `BudgetExceeded` and `llm_calls` shows `BUDGET_EXCEEDED` at zero cost

The tenant's model budget is spent (D47); those calls never left the platform.
The incident is still `Resolved`, not `Failed`: the report is kept with whatever
evidence the tools gathered, and its outcome says the investigation was cut
short. Notification channels hear it as `investigation_completed` with that
outcome, and the `budget_threshold` notification at 100% will already have gone
out (D48).

`GET /v1/budget` shows the limit, the period start and the spend. Either wait for
the period to turn (monthly budgets reset on the first of the UTC month), raise
the limit with `PUT /v1/budget`, or remove it with `DELETE /v1/budget`, then
retry the incident.

### A notification channel stopped receiving messages

The worker logs the channel id and the HTTP status, never the URL. Common causes:

- `NOTIFICATION_DELIVERY_FAILED` on `/test` with a 4xx from the chat tool: the
  webhook was revoked or its flow turned off. Create a new one and replace the
  secret.
- The test is refused before sending: in production the URL must be HTTPS to
  Slack's or Teams' own hosts, or to a host in
  `Notifications:AllowedWebhookHosts`, and never an IP address.
- Nothing arrives for completions at all: check that the `notifier` consumer is
  running in the worker (`Notifications:Enabled`) and look for its rows in
  `consumer_delivery_attempts` and `outbox_messages` on
  `sre.incident.completed.v1`. A send that keeps failing follows the usual
  dead-letter path; channels that had already accepted the message are not sent
  it again on replay.
- A Teams channel subscribed only to `investigation_failed` hears nothing about
  a report cut short by the tenant budget: that investigation is `Resolved` with
  outcome `BudgetExceeded`, announced as `investigation_completed`, and the
  `budget_threshold` message covers it.

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

### Sending notifications to Slack or Teams

Create the webhook in the chat tool (a Slack incoming webhook, or a Teams
Workflows "post to a channel when a webhook request is received" flow), put its
URL in a Secret, and mount it under `Llm:CredentialFileRoot`
(`/var/run/secrets/sre-ai`) in the API and the investigation worker: the worker
sends, the API sends the test message. A `file://` reference is relative to that
root. Then register the channel by reference, never by URL (D48):

```bash
curl -X POST "$API/v1/notification-channels" -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{
    "kind": "teams",
    "display_name": "sre-oncall",
    "webhook_ref": "file://teams-oncall",
    "events": ["investigation_failed", "budget_threshold"]
  }'
curl -X POST "$API/v1/notification-channels/$ID/test" -H "Authorization: Bearer $TOKEN"
```

`events` defaults to all three. Managing channels needs the Operator or
PlatformAdmin role. A webhook rotates like a `file://` provider key (table
below); to change anything else about a channel, delete it and create it again.

### Turning a wrong report into an eval case

People record verdicts with `POST /v1/incidents/{id}/report/feedback`
(`Confirmed`, `PartiallyCorrect`, `Incorrect`, optionally the actual cause).
`sre_report_feedback_total{verdict,outcome}` shows the trend; the cases to learn
from are listed by:

```bash
curl "$API/v1/report-feedback?verdict=incorrect&since=2026-10-01T00:00:00Z" \
  -H "Authorization: Bearer $TOKEN"
```

For each one worth keeping:

1. Read the incident, its evidence and the report (`GET /v1/incidents/{id}`,
   `/evidence`, `/report?version=N`) beside the verdict's `actual_root_cause`.
2. Write a case in the shape of `tests/SreAiAgent.Evals/Datasets/incidents/v1/cases/`:
   the incident, the tool results as fixtures, the actual cause as the expected
   root-cause family, and the report's wrong conclusion as a forbidden claim.
   Redact by hand: hostnames, customer names, ids and anything else that should
   not live in the repository. Nothing does this automatically, and the
   verdict's free text is not redacted at all.
3. Put it in a **new dataset version** (`v2/`, copying `v1`'s cases), never in
   `v1`. A dataset's hash is part of every baseline built on it, and baselines
   are immutable; editing `v1` would make every existing baseline incompatible.
4. Bootstrap a baseline for the new version with the active release, then
   evaluate the next release against it (`tests/SreAiAgent.Evals/EVALS.md`).

### Backup and restore

Three logical databases, backed up together: `sre` (incidents, evidence,
reports, audit, the model control plane, the outbox and inbox, and
`__migrations_history`), `temporal` and `temporal_visibility`. Kafka holds
nothing that is not also in PostgreSQL: undelivered events are outbox rows, and
a consumer that re-reads a topic is deduplicated by the inbox. Dead letters on
`sre.deadletter.v1` are the exception; mirror that topic if it must survive a
broker loss.

```bash
# As the owner of each database, at the same point in time.
pg_dump --format=custom --file=sre.dump                 --dbname=sre
pg_dump --format=custom --file=temporal.dump            --dbname=temporal
pg_dump --format=custom --file=temporal_visibility.dump --dbname=temporal_visibility
```

Restore into empty databases owned by the same roles, then let the migration job
run: it applies only what `__migrations_history` says is missing, so a backup
taken by an older release upgrades in place.

```bash
pg_restore --exit-on-error --no-owner --role=<business owner> --dbname=sre sre.dump
```

Never restore the business database alone onto a running Temporal, or the other
way round; see "Coordinated recovery".

### Secret rotation

| Secret | How it is read | Rotation |
|---|---|---|
| Model provider key, `file://` reference | Read from the mounted file on every call | Update the Kubernetes Secret; the kubelet refreshes the mount within about a minute. No restart, no new revision |
| Model provider key, `env://` reference | Process environment, read on every call | Update the Secret, then a rolling restart of the API and both workers |
| A different credential *reference* | Part of the immutable provider revision | New revision → deployment version → assignment release → evaluate → activate, through the management API. Open incidents keep the snapshot they pinned |
| Kafka SASL credentials | `Kafka:SaslUsername` / `SaslPassword` at start-up | Add the new credential on the broker, update the Secret, rolling restart, then remove the old one |
| PostgreSQL runtime password | Connection string at start-up; pooled connections stay authenticated | `ALTER ROLE ... PASSWORD`, update the Secret, rolling restart promptly — new connections fail until each pod restarts |
| OIDC signing keys | The identity provider's JWKS | Nothing to do here; the API refreshes the key set when it meets an unknown key id |

Prefer `file://` for provider keys: it rotates without a restart and does not put
the key in the process environment.

### Retention

The `Retention` sweep runs every `Retention:IntervalMinutes` (60) and deletes
in batches of `Retention:BatchSize` (1000), so one sweep never holds a long
lock. It removes only plumbing:

| Rows | Kept for | Never deleted |
|---|---|---|
| Processed inbox messages | `InboxRetentionDays`, 14 | — |
| Dispatched outbox messages | `DispatchedOutboxRetentionDays`, 7 | Dead-lettered rows |
| Settled delivery attempts | `DeliveryAttemptRetentionDays`, 7 | Unsettled attempts |
| Notification delivery claims | `NotificationClaimRetentionDays`, 62 | — |

Incidents, timeline, evidence, findings, reports, LLM and tool audit rows and the
model control plane are never deleted by the platform. The inbox window must
outlive the longest broker redelivery, or a late redelivery is processed twice;
the outbox window must outlive the window in which an operator may want to
replay a message.

### Coordinated recovery

Restore in dependency order, from backups taken at the same point in time:

1. PostgreSQL: `sre`, `temporal`, `temporal_visibility`.
2. Temporal, against its restored databases.
3. The broker. An empty broker is acceptable: the outbox re-dispatches what was
   not yet sent, and consumers start from the earliest offset.
4. The migration job, then the API, then both workers.

Then reconcile what the two stores disagree about:

- An incident `Investigating` with no running workflow (the business database is
  newer than Temporal's): `POST /v1/incidents/{id}/retry`. The retry is refused
  while a workflow is running, so it cannot double one up.
- A running workflow whose incident does not exist (Temporal is newer):
  `temporal workflow terminate --workflow-id incident-<id>`. It would otherwise
  fail at `LoadIncident` and hold its id.
- `SELECT count(*) FROM outbox_messages WHERE dispatched_at IS NULL` settles at
  zero once the broker is reachable.

### Load and soak

```bash
k6 run -e BASE_URL=http://localhost:8080 eng/load/alert-storm.js                    # 5 min ramp, 20 min soak
k6 run -e BASE_URL=... -e TOKEN=<jwt> -e TENANT=<tenant> eng/load/alert-storm.js   # anything but compose
docker run --rm --network host -v "$PWD/eng/load:/scripts:ro" grafana/k6 run /scripts/alert-storm.js
```

The script ramps from 1 to 10 alerts per second and holds, drawing fingerprints
from a pool of `FINGERPRINTS` (50) so most deliveries fold into an open incident
and a minority open one. It fails on more than 1% failed requests or a p95 above
500 ms. Outside k6, during the run: the outbox pending count stays flat, no
worker restarts, worker memory levels off, and each fingerprint has exactly one
incident.

The API's rate limit is per caller, `Security:RateLimitPerMinute` (600) in a
fixed one-minute window, and an Alertmanager is one caller. At the script's
10 per second the limit is exactly met, so an occasional `429` at a window
boundary is expected; Alertmanager retries it. An installation whose alert
volume from one sender exceeds the limit has to raise it, or it trades alerts
for retries.

Last run (2026-10-05, compose, 1 min ramp and 3 min hold rather than the full
25): 2,129 requests, p95 4.5 ms, one `429`; the 2,128 accepted alerts produced
50 incidents with an alert count summing to 2,128, every one resolved, the
outbox drained to zero, no restarts, workers between 150 and 220 MiB.

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
