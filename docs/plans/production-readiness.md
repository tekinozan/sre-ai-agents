# Production readiness execution plan

> **Status:** Phases 1–4 and local deployment follow-up complete; Phase 5 not started.
> **Source of truth:** this document. The root `PRODUCTION_READINESS_PLAN.md` it once pointed at was never committed and is lost; the per-step specifications that survive are the ones recorded below and in the commits that landed each phase.
> **Scope:** This document governs execution phasing, ordering dependencies, affected files, acceptance criteria, open questions, and completion tracking.
> **Last updated:** 2026-09-09.

## Status

| Phase | Plan steps | Status |
|---|---|---|
| [Phase 1 — Tenant isolation on write paths](#phase-1--tenant-isolation-on-write-paths) | Steps 1, 2, 3, 4 | Complete |
| [Phase 2 — Messaging: transport security and bounded poison messages](#phase-2--messaging-transport-security-and-bounded-poison-messages) | Steps 5, 6 | Complete |
| [Phase 3 — Operational surface: retention, health, probes](#phase-3--operational-surface-retention-health-probes) | Steps 7, 8, 9 | Complete |
| [Phase 4 — Outbound resilience and a production configuration that boots](#phase-4--outbound-resilience-and-a-production-configuration-that-boots) | Steps 10, 11 | Complete |
| [Phase 5 — Proof and operations](#phase-5--proof-and-operations) | Steps 12, 13, 14 | Not started |
| [Deployment follow-up — local Compose PostgreSQL consolidation](#deployment-follow-up--local-compose-postgresql-consolidation) | Local stack reliability follow-up | Complete |

---

## Cross-phase constraints

1. **Phase 2 before Phase 3:** `KafkaHealthCheck` in Phase 3 (step 8) constructs an `IAdminClient` and directly calls `KafkaClientConfiguration.ApplySecurity`, which is introduced in Phase 2 (step 5).
2. **Tenant fallback removal split across phases:** Step 3 introduces tenant binding at ingestion in Phase 1, but `AlertReceivedConsumer`'s `"default"` fallback removal must land in Phase 2 (step 6). Unlabelled alerts can only be rejected once the consumer dead-letter path exists on `KafkaConsumerService<TPayload>`.
3. **Health checks before manifests:** The shared health checks and worker HTTP listeners in step 8 (Phase 3) must compile and register before the Kubernetes, Helm, and Compose probe additions in step 9 are applied.
4. **Replay fixture requires a running stack:** The Phase 5 (step 12) workflow history fixture (`investigation-history.json`) cannot be hand-authored; it must be recorded from a running Compose stack driving an alert to completion.
5. **Independent build gates:** Every phase must independently leave `bash eng/ci.sh` green with zero warnings and zero regressions on the 371 baseline tests.

---

## Deployment follow-up — local Compose PostgreSQL consolidation

> **Status (2026-09-09):** Complete. Local multi-PostgreSQL topology replaced with a single PostgreSQL 17 service, dedicated DatabaseMigrator, idempotent POSIX /bin/sh provisioning, parameterized host ports, and non-destructive volume migration. All scenarios verified in isolated Docker Compose projects with volumes preserved.

### Observed Verification Evidence

1. **Fresh-install scenario (`sre-fresh`, ports 28080/25432/...):**
   - Single PostgreSQL 17 instance hosts `sre` (owner `businessowner`), `temporal` (owner `temporal`), and `temporal_visibility` (owner `temporal`).
   - `sre_admin` is bootstrap administrator (`rolsuper: true`); `businessowner` and `temporal` are non-superusers (`rolsuper: false`, `rolinherit: false`, `rolcreaterole: false`, `rolcreatedb: false`, `rolcanlogin: true`).
   - `migrate` service applied all 4 migrations (`__migrations_history` verified) and exited with code 0 before `api` and workers started.
   - Runtime hosts run with `Database__MigrateOnStartup=false`.
   - Provisioning rerun idempotency proved zero drift across `pg_authid` password hashes, `pg_roles` flags, `pg_database` owners, migration count, and a data sentinel row.
   - End-to-end alert `fresh-alert-1` accepted with `202 Accepted`, incident transitioned to `Resolved`, root cause report completed ("Latency rose alongside connection pool contention; the database layer is the leading candidate."), Temporal workflow `incident-01a086ba-201f-7c60-8955-a9982edc8e4e` reached `Completed`, outbox pending count was 0, worker restarts were 0, and `sre_trace_attribute_rejected_total` was 0.

2. **Migration failure gate scenario (`sre-gate`, ports 38080/35432/...):**
   - Temporary invalid migration credential override caused `migrate` service to fail immediately (`password authentication failed for user "invalid_user"`, exit code 1).
   - `api` remained in `Created` state and never started, proving the deployment gate invariant in Compose.
   - The Helm two-phase promotion gate is verified through fail-fast command chaining (`set -euo pipefail` with explicit `&&`) and static manifest rendering; no live Kubernetes release was executed.
   - Stack cleanly stopped without `-v`; volume `sre-gate_postgres-data` retained.

3. **Legacy transition scenario (`sre-clone`, ports 48080/45432/...):**
   - Cloned source volume `sre_agents_sre-postgres-data` read-only into `sre-clone_postgres-data` with empty-target assertion.
   - Target stack booted with legacy `sre` administrator; provisioning additively created `temporal` role and `temporal`/`temporal_visibility` databases while preserving existing `sre` database and ownership.
   - Migration succeeded on the clone (exit 0).
   - Source volume SHA256 checksums (1,337 files) verified byte-for-byte identical before and after.
   - Stack cleanly stopped without `-v`; volumes `sre-clone_postgres-data` and `sre_agents_sre-postgres-data` retained.

4. **Helm, raw manifests, and database bootstrap:**
   - Zero `Secret` objects or `REPLACE_ME` tokens in chart or rendered output; external secrets contract enforced.
   - `migration-job.yaml` runs under pre-existing default ServiceAccount (`automountServiceAccountToken: false`) with no dependency on chart-created SAs or ConfigMaps.
   - NetworkPolicy egress verified: in production, `api`, `alert-worker`, and `investigation-worker` possess `0.0.0.0/0` TCP 443 provider egress, while `migrator` is strictly restricted to DNS and PostgreSQL. Base/development leaves provider egress empty for scripted isolation.
   - Two-phase split-role bootstrap verified on PostgreSQL 17.10 (x86_64-pc-linux-musl) across two explicit connections with zero role-membership mutation:
     * Existing table SELECT, INSERT, UPDATE, DELETE: PASS
     * Existing identity/sequence-backed insert and sequence access: PASS
     * Future table INSERT/SELECT and future sequence/identity after default privileges: PASS
     * Rerun idempotency of both scripts without altering membership state or standing ADMIN OPTION: PASS
     * Runtime denial of CREATE TABLE, CREATE SCHEMA, and SET ROLE migrator: PASS
     * In-transaction failure rollback preserving state without membership leakage: PASS
   - `eng/render-k8s.sh` and `eng/check-k8s-render.sh` completed with Docker container fallback (`alpine/helm:3.17.1`) and pass drift checks with exit 0.
   - Live CNI, in-cluster Kubernetes, and FQDN egress gateway validation were NOT performed (no live cluster available in environment; documented as environment acceptance checks).
---

## Phase 1 — Tenant isolation on write paths

### Scope
Covers plan **Steps 1, 2, 3, and 4**. Closes tenant isolation gaps across all mutating HTTP API paths: approval requests (`ApproveAsync`, `RejectAsync`, `EditAsync`), incident lifecycle commands (`InvestigateAsync`, `RetryAsync`, `CancelAsync`), and alert ingestion (`AlertNormalizer.BindTenant`). Adds integration and unit tests proving tenant isolation and binding.

### Why these steps travel together
They form a coherent security perimeter around API write actions. Read endpoints already enforce caller tenant checks; write endpoints currently omit tenant scoping. Implementing steps 1–3 closes the API surface, while step 4 proves the invariants through unit and database-backed integration tests. They require no database schema migrations and have zero dependencies on infrastructure, messaging, or deployment changes.

### Ordering dependency
- **Prerequisites:** None. Independent of all subsequent phases.
- **Successors:** Phase 2 consumes the ingestion invariant established in step 3 to remove consumer fallbacks safely.

### Exact files touched
- `src/SreAiAgent.Application/Abstractions/Persistence/IApprovalStore.cs` — add `tenant` parameter to `GetAsync`.
- `src/SreAiAgent.Infrastructure/Persistence/Repositories/ApprovalStore.cs` — implement `GetAsync(tenant, ...)` with tracking and subquery against `Incidents.Tenant`.
- `src/SreAiAgent.Application/UseCases/ApprovalDecisionHandler.cs` — add `tenant` parameter to `DecideAsync` and `EditAsync`, forward to `approvals.GetAsync`.
- `src/SreAiAgent.Api/Endpoints/ApprovalEndpoints.cs` — inject and forward `principal.Tenant()` across approval mutation endpoints.
- `src/SreAiAgent.Application/UseCases/IncidentLifecycleHandlers.cs` — add `tenant` parameter to `StartInvestigationHandler`, `RetryInvestigationHandler`, and `CancelInvestigationHandler`, checking against `incident.Tenant`.
- `src/SreAiAgent.Api/Endpoints/IncidentEndpoints.cs` — inject `ClaimsPrincipal principal` on `InvestigateAsync` and `RetryAsync`, forward `principal.Tenant()` across all three lifecycle endpoints.
- `src/SreAiAgent.Infrastructure/Alerts/AlertNormalizer.cs` — add `BindTenant(CreateIncidentRequest, string)` static gate.
- `src/SreAiAgent.Api/Endpoints/AlertEndpoints.cs` — invoke `BindTenant` using `context.User.Tenant()`, return `Problem` on failure.
- `tests/SreAiAgent.IntegrationTests/TenantIsolationTests.cs` (new) — database-backed cross-tenant rejection tests for lifecycle handlers and approval decisions.
- `tests/SreAiAgent.Infrastructure.Tests/AlertTenantBindingTests.cs` (new or extended under `tests/SreAiAgent.Infrastructure.Tests/`) — unit test suite for `AlertNormalizer.BindTenant`.

### Acceptance criteria
- Accessing or mutating another tenant's incident or approval request returns `404 Not Found` (`ApplicationErrorCodes.IncidentNotFound` or `ApplicationErrorCodes.ApprovalNotFound`), never disclosing resource existence.
- Alert ingestion binds `labels["tenant"]` from `principal.Tenant()`; payloads naming a different tenant are refused with `403 Forbidden` (`ApplicationErrorCodes.Forbidden`).
- `IIncidentRepository.GetAsync` signature remains unchanged so internal `InvestigationActivities` inside Temporal workflows are unaffected.
- `AlertReceivedConsumer` retains its fallback during this phase (removed strictly in Phase 2 step 6).

### Verification
1. `bash eng/ci.sh` (or `powershell -ExecutionPolicy Bypass -File eng/ci.ps1`) passes cleanly.
2. `bash eng/ci.sh --with-integration` passes, executing the new `TenantIsolationTests` under Postgres Testcontainers.
3. Alert ingestion curl verification against Compose stack:
   ```bash
   curl -i -X POST http://localhost:8080/v1/alerts/alertmanager \
     -H 'Content-Type: application/json' \
     -d '{"status":"firing","fingerprint":"tenant-probe-1","labels":{"alertname":"HighLatency","tenant":"acme"},"annotations":{"summary":"probe"}}'
   ```
   Under development authentication (tenant `local`), payload naming `acme` returns `403 Forbidden`. Repeating with `"tenant":"local"` or with the label omitted returns `202 Accepted` with `tenant` assigned to `local`.

### Open questions and notes
- None. Starting state verified and types confirmed on disk.

---

## Phase 2 — Messaging: transport security and bounded poison messages

### Scope
Covers plan **Steps 5 and 6**. Configures Kafka SASL/TLS transport security across producer and consumer clients. Introduces durable attempt tracking (`ConsumerDeliveryAttemptEntity`, EF migration, `IDeliveryAttemptStore`), bounded consumer retry, dead-lettering with settlement to `sre.deadletter.v1`, and removes the `"default"` tenant fallback in `AlertReceivedConsumer`.

### Why these steps travel together
Step 5 secures the messaging wire protocol. Step 6 establishes message consumption bounds and error handling. Unlabelled alerts can only be rejected by the consumer once the consumer-side dead-letter infrastructure exists to capture poison payloads. Furthermore, `KafkaClientConfiguration` created in step 5 is directly utilized in both producer and consumer pipelines.

### Ordering dependency
- **Prerequisites:** Phase 1 (tenant ingestion binding in step 3 guarantees alerts ingested via API carry `labels["tenant"]`).
- **Successors:** Phase 3 requires `KafkaClientConfiguration.ApplySecurity` from step 5 for `KafkaHealthCheck`.

### Exact files touched
- `src/SreAiAgent.Infrastructure/Configuration/SreOptions.cs` — add SASL/TLS properties and `IValidatableObject` rules to `KafkaOptions`.
- `src/SreAiAgent.Infrastructure/Messaging/KafkaClientConfiguration.cs` (new) — centralized helper mapping `KafkaOptions` onto Confluent `ClientConfig`.
- `src/SreAiAgent.Infrastructure/Messaging/KafkaMessageProducer.cs` — apply security options to `ProducerConfig`.
- `src/SreAiAgent.Infrastructure/Messaging/KafkaConsumerService.cs` — apply security options to `ConsumerConfig`; inject `IMessageProducer` and `TimeProvider`; implement durable failure counting, dead-letter publishing, and settlement.
- `src/SreAiAgent.Infrastructure/Persistence/Entities/InfrastructureEntities.cs` — add `ConsumerDeliveryAttemptEntity`.
- `src/SreAiAgent.Infrastructure/Persistence/Configurations/MessagingConfigurations.cs` — configure `consumer_delivery_attempts` table, compound key, lengths, and indices (`LastFailedAt`, `SettledAt`).
- `src/SreAiAgent.Infrastructure/Persistence/SreDbContext.cs` — register `DbSet<ConsumerDeliveryAttemptEntity> ConsumerDeliveryAttempts`.
- `src/SreAiAgent.Application/Abstractions/Messaging/IDeliveryAttemptStore.cs` (new) — define attempt recording and settlement contract.
- `src/SreAiAgent.Infrastructure/Persistence/Repositories/DeliveryAttemptStore.cs` (new) — implement upsert and settlement using independent scope/context.
- `src/SreAiAgent.Infrastructure/PersistenceDependencyInjection.cs` — register `IDeliveryAttemptStore`.
- `src/SreAiAgent.Infrastructure/Persistence/Migrations/*_ConsumerDeliveryAttempts.cs` (new) — EF Core migration.
- `src/SreAiAgent.Infrastructure/Messaging/Consumers/AlertReceivedConsumer.cs` — remove `"default"` fallback in `TenantFor`, failing on missing tenant label.
- `tests/SreAiAgent.Infrastructure.Tests/KafkaSecurityConfigurationTests.cs` (new) — test options validation and `KafkaClientConfiguration.ApplySecurity`.
- `tests/SreAiAgent.IntegrationTests/DeliveryAttemptStoreTests.cs` (new) — integration tests for independent failure recording and settlement.

### Acceptance criteria
- `KafkaOptions` rejects incomplete SASL configuration at startup via data validation.
- `KafkaClientConfiguration.ApplySecurity` uniformly configures `ProducerConfig`, `ConsumerConfig`, and `AdminClientConfig`.
- Failing consumer handlers record attempts durably in an independent database transaction; handler rollbacks do not roll back the attempt increment.
- Reaching `MaxDeliveryAttempts` dead-letters the message to `sre.deadletter.v1` with source identity (topic, partition, offset, consumer name) and commits the consumer offset.
- Completed handling or dead-lettering records settlement (`SettledAt`, `SettlementKind`) rather than deleting rows prematurely.
- Unlabelled alert messages throw an exception, reach the attempt ceiling, and dead-letter rather than falling back to `"default"`.

### Verification
1. `bash eng/ci.sh` passes.
2. `KafkaSecurityConfigurationTests` verifies config mapping and SASL validation without requiring a broker.
3. `bash eng/ci.sh --with-integration` validates `ConsumerDeliveryAttempts` migration application and store behavior.
4. Compose stack poison message test:
   ```bash
   docker compose -f deploy/docker/compose.yaml exec redpanda \
     rpk topic produce sre.alert.received.v1 -X brokers=localhost:9092
   # produce envelope without "tenant" label
   docker compose -f deploy/docker/compose.yaml exec redpanda \
     rpk topic consume sre.deadletter.v1 -X brokers=localhost:9092 -n 1
   ```
   Message is dead-lettered after 5 attempts; `consumer_delivery_attempts` records settled attempt; alert worker remains healthy.

### Open questions and notes
- **PostgreSQL `RETURNING attempt_count` non-composable query:** `Database.SqlQuery<int>` with raw `INSERT ... RETURNING` is treated by EF Core as non-composable SQL. Calling `.SingleAsync()` directly on `IQueryable<T>` triggers EF to compose an outer `SELECT ... FROM (INSERT ...) AS t` wrapper which fails in PostgreSQL. Resolved by streaming with `AsAsyncEnumerable().WithCancellation(...)`, which bypasses outer query composition and reads directly from `DbDataReader`.

---

## Phase 3 — Operational surface: retention, health, probes

### Scope
Covers plan **Steps 7, 8, and 9**. Implements `RetentionService` and `RetentionOptions` to prune expired infrastructure rows (`inbox_messages`, `outbox_messages`, `idempotency_keys`, `consumer_delivery_attempts`). Moves `DatabaseHealthCheck` to Infrastructure, adds `KafkaHealthCheck` and `TemporalHealthCheck`. Converts both worker hosts to `Microsoft.NET.Sdk.Web` with a minimal HTTP listener on port 8081. Adds Kubernetes/Helm probes, NetworkPolicy ingress, and Compose healthchecks.

### Why these steps travel together
They establish host observability and operational lifecycle boundaries. Health checks must be implemented and exposed on the host HTTP surface (step 8) before container manifests and Kubernetes deployment specs can probe them (step 9). Retention runs as a hosted background service on the alert worker alongside its new health listener.

### Ordering dependency
- **Prerequisites:** Phase 2 (step 8's `KafkaHealthCheck` relies on `KafkaClientConfiguration.ApplySecurity` from step 5; step 7 cleans settled rows from step 6).
- **Successors:** Phase 4 and Phase 5 rely on responsive health endpoints and probeable worker containers.

### Exact files touched
- `src/SreAiAgent.Infrastructure/Configuration/SreOptions.cs` — add `RetentionOptions`.
- `src/SreAiAgent.Infrastructure/DependencyInjection.cs` — register `RetentionOptions` validation.
- `src/SreAiAgent.Infrastructure/Persistence/RetentionService.cs` (new) — `PeriodicTimer` background service executing batch deletes.
- `src/SreAiAgent.Infrastructure/PersistenceDependencyInjection.cs` — register `AddSreRetention`.
- `src/SreAiAgent.Api/Health/DatabaseHealthCheck.cs` — delete API-specific file.
- `src/SreAiAgent.Infrastructure/Health/DatabaseHealthCheck.cs` (new location) — shared database health check.
- `src/SreAiAgent.Infrastructure/Health/KafkaHealthCheck.cs` (new) — admin client broker metadata check.
- `src/SreAiAgent.Infrastructure/HealthDependencyInjection.cs` (new) — register shared health checks.
- `src/SreAiAgent.Workflow/Health/TemporalHealthCheck.cs` (new) — Temporal client connection health check.
- `src/SreAiAgent.Workflow/WorkflowDependencyInjection.cs` — add `AddSreWorkflowHealthCheck`.
- `src/SreAiAgent.Api/Program.cs` — wire shared infrastructure and workflow health checks.
- `src/SreAiAgent.Api/Endpoints/HealthEndpoints.cs` — filter `/health/ready` to `ready` tag.
- `src/SreAiAgent.AlertWorker/SreAiAgent.AlertWorker.csproj` — update SDK to `Microsoft.NET.Sdk.Web`.
- `src/SreAiAgent.AlertWorker/Program.cs` — convert to `WebApplication`, listen on port 8081, map `/health/live` and `/health/ready`, register retention service.
- `src/SreAiAgent.InvestigationWorker/SreAiAgent.InvestigationWorker.csproj` — update SDK to `Microsoft.NET.Sdk.Web`.
- `src/SreAiAgent.InvestigationWorker/Program.cs` — convert to `WebApplication`, listen on port 8081, map `/health/live` and `/health/ready`.
- `deploy/k8s/workers.yaml` — add ports, liveness, readiness, and startup probes to workers.
- `deploy/helm/sre-ai-agent/templates/workers.yaml` — add probe definitions matching the API template.
- `deploy/k8s/networkpolicy.yaml` — add port 8081 ingress rules for monitoring namespace.
- `deploy/docker/compose.yaml` — add curl healthchecks on port 8081 to workers.
- `tests/SreAiAgent.IntegrationTests/RetentionServiceTests.cs` (new) — verify cutoff horizons and batch pruning.

### Acceptance criteria
- `RetentionService` purges only expired/settled infrastructure records in chunked batches (`ctid` subqueries with `LIMIT`); domain entities (incidents, evidence, reports, audit, dead letters) are untouched.
- Both workers expose `/health/live` (unconditional) and `/health/ready` (database, Kafka, and Temporal where applicable) on port 8081.
- API and worker readiness fail when Kafka or the database is down; liveness continues returning 200.
- Manifests (Kubernetes, Helm, Docker Compose) successfully probe port 8081 without permission or connection errors.

### Verification
1. `bash eng/ci.sh` builds cleanly and passes all tests.
2. `bash eng/ci.sh --with-integration` runs `RetentionServiceTests` against Postgres.
3. Compose stack probe checks:
   - `docker compose ps` shows `api`, `alert-worker`, and `investigation-worker` as `healthy`.
   - `docker compose exec investigation-worker curl -fsS http://localhost:8081/health/ready` returns `Healthy`.
   - Stopping Redpanda (`docker compose stop redpanda`) causes `/health/ready` on both API and workers to return 503 within 20s, while `/health/live` remains 200.

### Open questions and notes
- **EventId 7401 collision:** Resolved. `AlertReceivedConsumer.LogRejected` already owns EventId 7401; `RetentionService` uses EventId 7501 for its sweep summary logger and 7502 for sweep failures.
- **Temporal `RpcException` namespace:** Confirmed on disk in Temporalio 1.18.0 assembly as `Temporalio.Exceptions.RpcException`. Caught specifically by `TemporalHealthCheck`.
- **CNI probe traffic:** Live CNI validation remains outstanding on a real cluster. Worker NetworkPolicy ingress currently permits the `monitoring` namespace only. Target-CNI kubelet source behavior remains an environment acceptance check; if the target CNI enforces node source addresses, a future explicit CIDR rule and configuration value must be added and validated without weakening default-deny.
---

## Phase 4 — Outbound resilience and a production configuration that boots

### Scope
Covers plan **Steps 10 and 11**. Attaches `Microsoft.Extensions.Http.Resilience` standard resilience pipeline to the outbound model HTTP client (`ProviderHttpClients.Chat`) with configurable attempt timeout, circuit breaking, and `Retry-After` header adherence. Aligns retry responsibilities between pipeline and `LlmGateway`. Produces `values.production.yaml` for Helm, wires production secrets, and updates deployment manifests to eliminate hardcoded test/scripted configurations.

### Why these steps travel together
They turn the platform into a production-deployable artifact. Step 10 prevents external model provider throttling or outages from cascading through worker threads. Step 11 provides the configuration, secret references, and deployment overrides that enable booting with real providers (OpenAI, Prometheus, Loki, Kafka SASL) instead of mock stubs.

### Ordering dependency
- **Prerequisites:** Phase 2 (Kafka security configuration options), Phase 3 (worker probe and health surface configurations).
- **Successors:** Phase 5 load testing and replay scenarios exercise the hardened pipeline and configuration.

### Exact files touched
- `src/SreAiAgent.Infrastructure/Configuration/SreOptions.cs` — add `RequestTimeoutSeconds` to `LlmOptions`.
- `src/SreAiAgent.Infrastructure/DependencyInjection.cs` — configure `AddStandardResilienceHandler` on `ProviderHttpClients.Chat` with timeout, circuit breaker, and `ShouldRetryAfterHeader`.
- `src/SreAiAgent.Infrastructure/Llm/LlmGateway.cs` — update backoff calculation to exponential with jitter.
- `deploy/helm/sre-ai-agent/templates/config.yaml` — parameterize `Llm__Provider`, `Kafka__SecurityProtocol`, `Kafka__SaslMechanism`, `Retention__Enabled`.
- `deploy/helm/sre-ai-agent/values.yaml` — configure default `llm.provider`.
- `deploy/helm/sre-ai-agent/values.production.yaml` (new) — production settings for Prometheus, Loki, incluster Kubernetes, OpenAI, SASL Kafka, and retention.
- `deploy/k8s/config.yaml` — update stale comments regarding in-cluster adapters and add SASL credentials to secret examples.
- `tests/SreAiAgent.Infrastructure.Tests/ProviderResilienceTests.cs` (new) — unit tests validating HTTP retry pipeline, 429 `Retry-After`, and circuit breaker behavior.

### Acceptance criteria
- Single LLM HTTP attempt is bounded by `RequestTimeoutSeconds` (default 60s), below caller total timeout.
- HTTP 429 responses with `Retry-After` are respected by the HTTP resilience handler; transport errors are retried at the transport layer, not duplicated by `LlmGateway`.
- `LlmGateway` retries only semantic failures (truncated output, schema mismatches) using exponential jittered backoff.
- `helm template deploy/helm/sre-ai-agent -f deploy/helm/sre-ai-agent/values.production.yaml` renders cleanly, references external secrets for passwords/keys via `secretKeyRef`, and contains zero plaintext credentials.

### Verification
1. `bash eng/ci.sh` passes, including `ProviderResilienceTests`.
2. Provider resilience tests assert:
   - Stub returning 429 with `Retry-After: 0` followed by 200 succeeds with request count = 2.
   - Stub returning persistent 503 fails with `LLM_UNAVAILABLE` without excessive duplicate retries.
3. Helm template rendering check:
   ```bash
   helm template sre-agent deploy/helm/sre-ai-agent -f deploy/helm/sre-ai-agent/values.production.yaml
   ```
   Renders real providers, Kafka SASL settings, worker probe blocks, and secretKeyRefs without plaintext credentials.

### Open questions and notes
- **Resilience API property naming:** Verify `Microsoft.Extensions.Http.Resilience` 10.9.0 property names (`ShouldRetryAfterHeader`, `AttemptTimeout.Timeout`, `TotalRequestTimeout.Timeout`, `CircuitBreaker.SamplingDuration`) during implementation.
- **Live credential checks outstanding:** A successful minimal call through the real OpenAI provider using its configured `secretKeyRef`-sourced credential, and a connection to a real SASL/TLS Kafka broker using the configured SASL mechanism, were not exercised — no real OpenAI API key or SASL/TLS broker credentials are available in this environment. Code path, options validation, and Helm secret wiring are implemented and unit/render-tested; the authenticated live calls themselves remain production-environment acceptance checks to run once real credentials exist.

---

## Phase 5 — Proof and operations

### Scope
Covers plan **Steps 12, 13, and 14**. Records a deterministic workflow history fixture from a live run and adds an offline Temporal workflow replay test (`WorkflowReplayer`). Creates a k6 alert-storm load/soak script. Expands `docs/RUNBOOK.md` with operational procedures for backup and restore, secret and credential rotation, retention behavior, and coordinated cluster disaster recovery.

### Why these steps travel together
They provide operational validation, durability guarantees, and runbooks for human operators before production traffic commences. They test the system as an integrated whole and document recovery runbooks that depend on choices finalized in Phases 1–4.

### Ordering dependency
- **Prerequisites:** Phases 1–4 must be complete. The replay history fixture (step 12) must be captured from a running Compose stack with the full pipeline intact.

### Exact files touched
- `tests/SreAiAgent.Workflow.Tests/Fixtures/investigation-history.json` (new) — recorded execution history of `TemporalInvestigationWorkflow`.
- `tests/SreAiAgent.Workflow.Tests/SreAiAgent.Workflow.Tests.csproj` — include fixture in output directory (`PreserveNewest`).
- `tests/SreAiAgent.Workflow.Tests/WorkflowReplayTests.cs` (new) — deterministic replay test using `WorkflowReplayer`.
- `eng/load/alert-storm.js` (new) — k6 load and soak test script.
- `docs/RUNBOOK.md` — add sections: "Backup and restore", "Secret rotation", "Retention", "Coordinated recovery", and "Load and soak".

### Acceptance criteria
- `WorkflowReplayTests` runs offline in default CI without requiring Docker or a running Temporal server, proving backward compatibility of `TemporalInvestigationWorkflow`.
- k6 script ramps 1 → 10 requests/second over 5 minutes and sustains for 20 minutes with thresholds `http_req_failed < 0.01` and `http_req_duration p(95) < 500ms`.
- `docs/RUNBOOK.md` defines concrete procedures for PostgreSQL backup/restore (`pg_dump` with `__migrations_history`), credential rotation for Kafka and LLM connections, retention expectations, and coordinated recovery sequence.

### Verification
1. `bash eng/ci.sh` passes, including offline `WorkflowReplayTests`.
2. Execute the full end-to-end recipe from `docs/agents/recipes/verify-end-to-end.md`:
   - Incident transitions from open to `Resolved`/`Completed`.
   - Single Jaeger trace spans API, worker, and tools.
   - Outbox messages drain to 0.
   - Metric `sre_trace_attribute_rejected_total` is 0.
3. Load soak run (when k6 is installed):
   ```bash
   k6 run -e BASE_URL=http://localhost:8080 -e TOKEN=<token> eng/load/alert-storm.js
   ```
   Thresholds pass, outbox pending count stays flat, workers do not OOM or restart.

### Open questions and notes
- **`WorkflowHistory.FromJson` factory:** In `Temporalio` 1.18.0, check `Temporalio.Common.WorkflowHistory` for the exact JSON deserialization method. If `WorkflowHistory.FromJson` is unavailable, use `Temporalio.Api.History.V1.History.Parser.ParseJson(json)` to instantiate `WorkflowHistory`.
- **History capture format:** Ensure the recorded JSON history fixture is the normalized history proto representation rather than a Temporal CLI envelope wrapper.
- **Cross-service trace continuity (outstanding, blocks final sign-off):** Verifying Phase 4 against the Compose stack (`docs/agents/recipes/verify-end-to-end.md`) found that one alert-to-report run produces spans (`classify`, `route`, `agent.execute`, `llm.call`, `tool.execute`, `correlate`) scattered across several different Jaeger trace ids rather than the single trace spanning API, alert-worker, and investigation-worker that this document's own Phase 5 verification step 2 requires ("Single Jaeger trace spans API, worker, and tools"); some of those spans carry no parent reference at all. This predates Phase 4's own changes — nothing in steps 10–11 touches tracing, the sampler, or context propagation — so it was not diagnosed or fixed as part of Phase 4. It must be investigated and resolved, with root cause identified (likely context propagation across the Kafka message envelope and/or the Temporal activity boundary) and a regression test added, before production readiness as a whole can be signed off; Phase 5's own step 2 verification cannot pass while this stands.
