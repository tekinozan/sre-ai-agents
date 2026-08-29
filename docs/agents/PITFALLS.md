# Pitfalls

Defects this platform actually produced, and what now stops them recurring.

Every one of these compiled. Most passed the unit tests. They were found by
running the API, the workers, PostgreSQL, Redpanda, Temporal and Jaeger together
and watching what the system did — which is why `AGENTS.md` opens with the rule it
does.

`docs/DECISIONS.md` records what the platform decided; this file records what it
got wrong first. The distinction matters when you are about to make a change that
looks obviously correct.

---

## Silent failures

These produced no error, no exception and no failing test. They are the reason
this file exists.

**An inbox row committed in its own transaction.** A later failure left a row
saying the message had been handled with no trace of the work. Redelivery — the
thing that should have repaired it — was skipped, and the effect was lost. The
exact outcome an inbox exists to prevent. Inbox rows and idempotency claims now
stage and commit with the caller's single `SaveChanges` (D25).

**Deduplication measured against the provider's clock.** An alert that had been
firing since the morning reported the morning as its time, so a freshly opened
incident fell outside its own active window and every notification opened another
incident. Detection time and platform time are now separate fields (D23).

**The idempotency claim outlived the incident.** At five minutes, a
quickly-resolved incident left its claim standing, so a legitimate recurrence
could neither fold into the closed incident nor take the claim, and became a
redelivery loop. Thirty seconds now (D24).

**The sampler was wrapped in `ParentBasedSampler`.** That wrapper only consults
its inner sampler for root spans, and `incident.investigation` never is one — it
starts inside the Kafka consumer span. At a 0.1 ingress ratio, nine investigations
in ten vanished, and Jaeger showed only `postgresql` spans from the worker.
`Infrastructure.Tests/SamplingTests` locks the fix (D27).

**A span attribute outside the allowlist.** Dropped, deliberately, so a telemetry
mistake cannot fail an investigation. The only signal is
`sre_trace_attribute_rejected_total` leaving zero.

**An agent with no feature flag entry.** Loads, passes startup validation, never
runs — `defaults` is `state: disabled`. `ShippedConfigurationTests` now fails on it.

**An agent or tool the scripted model provider does not recognise.** Runs, spends
its budget, returns nothing. `ShippedConfigurationTests` now drives the scripted
provider with each released agent's allowlist and asserts it asks for something.

**Inline fan-out shared one `DbContext`** across agents running in parallel. Added
a per-agent scope hook.

**Two events published twice** — a hand-built `IncidentCreated` alongside the
domain event, and `InvestigationStarted` sharing a wire type with the request.

---

## Serialization

Four separate failures, all from the same root: a generated context only knows the
types it was told about. `docs/agents/recipes/change-a-serialized-type.md` is the
procedure.

**Types behind a custom converter are invisible to the generator.** `Result<T>`'s
eight success types went missing, and Temporal failed with `NoMetadataForType`
*after* the workflow had started.

**`VoidTaskResult` cannot be named.** A `Task`-returning `[WorkflowRun]` meant the
final workflow task failed forever: the incident resolved, then the workflow
retried on the way out, holding its workflow id so a retry of that incident was
refused as already running (D28).

**`Observation` threw on deserialize and `Confidence` silently became 0.** Two
custom converters and `JsonbRoundTripTests` now cover it.

**`required` members stop property initializers running.** Optional members on
`CreateIncidentRequest` arrived null or default, so investigations never started.

**A schema written from the wire contract rather than from the type.** The first
`AgentTurn` schema said `tool_name`; the type says `tool`. Every agent turn failed
validation. Caught because the test serialized the real type.

**The API served camelCase while the contracts specify snake_case.**

---

## Dependency injection

Found by booting the host, which is the only thing that finds them.

- `IReadOnlyToolRegistryProbe` and `IToolExecutionContextResolver` were declared
  and never implemented — the container could not build.
- Singleton `ToolGateway` and `LlmGateway` needed scoped dependencies. Split
  `ToolAdapterCatalog` out and made both scoped.
- `AddSreApplication` registered handlers requiring `IInvestigationWorkflow`, which
  the alert worker does not have. Split into `AddSreInvestigationUseCases`.
- `OnConfiguring` is incompatible with `AddDbContextPool`.
- `Database:MigrateOnStartup` was read by nothing until `DatabaseMigrator` existed.

---

## Build and infrastructure

- **`.editorconfig` was missing from the Docker context.** It carries the analyzer
  severities, so the container build applied defaults the repository had turned
  off and failed on rules CI never runs (D29).
- **`adduser` is not in the base image.** Use the built-in `app` user.
- **A heredoc turned `\n` into a real newline** and broke a `printf` entrypoint.
  The Dockerfile uses a symlink instead.
- **The compose healthcheck used `wget`, which is absent.** `curl` is installed.
- **`jsonb` reparses payloads.** `{"ok":true}` comes back as `{"ok": true}`; key
  order and whitespace are not preserved. Compare parsed JSON, and never hash a
  payload expecting the stored copy to match.
- **"Zero tests ran" is exit 8, in both directions.** The integration project in
  the default run, and the other seven in a trait-filtered run. Hence the untraited
  convention test in `IntegrationTests` and the scoped integration step in
  `eng/ci.sh`.

---

## Still true, still unverified

Honest gaps, not solved problems.

- **Temporal mid-workflow replay has never been reproduced.** The scripted
  provider finishes in about two seconds and the race could not be won by hand.
  Recovery across a worker restart is verified; replay of a partially executed
  workflow is not.
- **The approval path has never run.** Tables, endpoints, the workflow signal and
  the wait all exist, but nothing in V1 creates an `ApprovalRequest`, so none of
  that code has executed.
- **Kubernetes and database observability are still deterministic fakes.**
  Metrics and logs now have real adapters; those two do not, and
  `AddSreObservability` refuses any fake provider in Production. Production is
  therefore not yet a startable configuration, which is accurate rather than an
  oversight.
- **The Helm chart has never been rendered** and the Kubernetes manifests have
  never been applied to a cluster. Only the value references and YAML parsing are
  checked.
