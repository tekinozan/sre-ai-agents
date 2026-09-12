# Decision log

Why this platform is built the way it is. Each entry records the decision, the
reason, and where relevant what it costs, because a decision without its cost is
not a decision anyone can revisit.

Decisions taken during implementation are at the end, separated from the ones
taken up front, because several of them were forced by running the thing rather
than by designing it.

---

## Foundations

### D01 — `SreAiAgent.slnx` at the repository root

The .NET 10 XML solution format. It merges more cleanly than the legacy format,
which matters on a solution this many projects wide.

### D02 — Central Package Management with lock files

Every version is pinned once in `Directory.Packages.props`, transitive pinning is
on, and `packages.lock.json` is committed. CI restores with `--locked-mode`, so a
dependency cannot float between the build that was tested and the build that
ships. ARCHITECTURE.md 22 requires this.

### D03 — Repository-local `NuGet.config` that clears inherited sources

The machine this was built on had a local feed and a Visual Studio offline feed
registered globally. Either can satisfy a restore with a package nobody chose,
which makes the build irreproducible in a way that is invisible until it breaks
somewhere else.

### D04 — No mediator, no AutoMapper, no generic repository, no service locator

Handlers are plain classes registered directly. The indirection those libraries
add is paid on every request in allocations and reflection, and paid again by
whoever is stepping through a failure at three in the morning. ARCHITECTURE.md 12
already asks endpoints to delegate to a handler; that is the whole pattern.

### D05 — Aggregate-scoped repositories, `DbContext` as the unit of work

`IIncidentRepository` loads and stores one aggregate. A generic
`IRepository<T>` over an ORM that already is one hides the query capabilities
that make the ORM worth using and pushes every caller toward loading whole
aggregates when they wanted three columns. Read paths use `IIncidentQueries`,
which projects straight into response contracts.

### D06 — Invariants throw, expected failures are values

A domain rule violation is a `DomainException`; "incident not found" or "policy
denied" is a `Result` carrying a stable `ErrorCode`. The first is a bug, the
second is part of the contract. The API maps the second onto `ProblemDetails`
through one frozen catalogue, so a code cannot mean 409 in one endpoint and 500
in another.

### D07 — `TimeProvider` everywhere; `DateTime.UtcNow` is a banned symbol

Enforced by the banned-API analyzer, not by review. Every domain method that
needs the time takes it as a parameter, which is what makes the lifecycle tests
deterministic.

---

## Runtime shape

### D08 — Temporal in production, an in-process executor for development

`IInvestigationWorkflow` is the port. Writing a durable engine was never on the
table; what was decided is that the development executor runs *the same
activities in the same order*, so the two cannot drift. The sequence lives in
`InvestigationSequence` and only the fan-out strategy differs.

The in-process executor refuses to construct when the host is in Production, and
`AddSreWorkflow("temporal")` refused to fall back to it silently before the
Temporal adapter existed. A development convenience that quietly removes
durability is the kind of thing nobody notices until an incident is lost.

**Cost:** two implementations of the fan-out, and a class of Temporal-only defect
(serialization, determinism) that local runs cannot find. Three such defects were
found the first time the Temporal engine actually ran.

### D09 — One `IChatClient`, and it is scripted

V1 ships no real model provider. `LlmGateway` owns everything that matters —
budgets, timeouts, schema validation, retry classification, audit — and the
provider behind it is a deterministic script that returns schema-valid responses,
cites evidence it was actually given and requests tools the agent is actually
allowed.

That makes the whole pipeline runnable in CI with no network, no cost and no
flakiness. Adopting a real provider is one DI registration.

### D10 — Source-generated `System.Text.Json` everywhere

Reflection-based serialization is banned by analyzer. It allocates and walks type
metadata on a path that runs per alert, per outbox dispatch and per Kafka
message, and it is the mechanism by which an unexpected polymorphic payload gets
materialised.

**Cost, learned the hard way:** a generated context only knows the types it was
told about. Types behind a custom converter are invisible to the generator, which
is how `Result<T>`'s success types went missing and a workflow failed after it had
already started. Every such type is now named explicitly and a test converts each
one.

### D11 — `[LoggerMessage]` for all logging

No interpolation, no boxing, `CA1848` and `CA2254` are errors.

### D12 — Registries are frozen at startup

Prompts, models, agents, tools, flags and policies are loaded once into
`FrozenDictionary`. Startup validation fails the host rather than the first
incident.

### D13 — `ActivitySource` and `Meter` in the application layer

ARCHITECTURE.md 1.2 forbids OpenTelemetry *implementation* details above
infrastructure. `System.Diagnostics` is a BCL primitive, so the instruments live
with the code that records them and the SDK wiring lives in one file in
Infrastructure. Code that records a span has no idea whether anything is
listening, which is what lets the same assembly run in a test with no exporter.

### D14 — Span attributes go through an allowlist

`Activity.SetTag` is a banned symbol; `SetSafeTag` consults `TraceAttributes` and
drops anything else, counting the drop in
`sre_trace_attribute_rejected_total`. ARCHITECTURE.md 16.1 forbids prompts,
tokens and credentials on spans, and a tracing backend is a different trust
domain: more readers, longer retention, wider export.

Dropping is silent so a telemetry mistake cannot fail an investigation. The
counter is what makes the guarantee observable rather than merely asserted.

### D15 — Read-only and write tool registries are separate types

`IReadOnlyToolRegistry` and `IWriteToolRegistry`. V1 registers no implementation
of the second, so a component that asks for write capability cannot be
constructed at all. That is stronger than a runtime check, which somebody can
invert.

### D16 — xUnit v3 on Microsoft.Testing.Platform, NSubstitute, Shouldly

Shouldly rather than FluentAssertions, whose v8 licence is commercial.

### D17 — One prerelease pin: the Prometheus scrape exporter

`OpenTelemetry.Exporter.Prometheus.AspNetCore 1.18.0-beta.1`. ARCHITECTURE.md
12.1 requires `GET /metrics` and OpenTelemetry has no stable equivalent. It is
the only prerelease dependency.

### D18 — The layer rule is enforced twice

An MSBuild target fails the build if Domain, Contracts or Application declare an
infrastructure package, and an assembly-reference suite catches what arrives
transitively. Both were tested by deliberately introducing a violation.

---

## Decided during implementation

### D19 — Alert ingress lives on the API; ingestion work lives in a worker

ARCHITECTURE.md 10.1 requires provider adapters but 12.1 defines no endpoint that
feeds them. `POST /v1/alerts/{source}` normalises a provider payload at the edge
and stages a `CanonicalAlert` on the alert topic; the alert worker consumes it and
opens the incident.

Both ingestion paths converge on `CreateIncidentHandler`. Two code paths that
create incidents would eventually disagree about what counts as a duplicate, and
deduplication is the one rule that has to hold at every entry point.

### D20 — The alert topic carries a full `CanonicalAlert`

Every other message is thin: a notification plus the key needed to read current
state. An accepted alert has no stored state yet, so identifiers alone would
point at nothing.

### D21 — SSE tails the timeline table rather than an in-process channel

The API serves `/v1/incidents/{id}/events`; the investigation worker writes the
events. They are separate deployments, so an in-memory broadcast would work only
while they happened to be one process.

Cursor polling on a `PeriodicTimer` rather than `LISTEN`/`NOTIFY`, which is more
elegant but holds a dedicated connection per subscriber. An incident stream is
watched by a handful of operators for a few minutes.

### D22 — Workers export over OTLP; only the API exposes `/metrics`

A second HTTP listener whose only job is metrics complicates the one thing a
worker does. The collector is already the aggregation point.

### D23 — Detection time and record-keeping time are separate

`DetectedAt` belongs to the provider and can be hours old; `CreatedAt`,
`UpdatedAt` and `LastAlertAt` belong to the platform. Conflating them broke
deduplication: an alert firing since the morning reported the morning, so a
freshly opened incident fell outside its own active window and every notification
opened another one.

### D24 — The idempotency claim is short-lived

Thirty seconds. Its only job is to serialise two concurrent creates; once the
incident commits, the fingerprint lookup finds it and the claim is redundant. At
five minutes a quickly-resolved incident left its claim standing and a legitimate
recurrence became a redelivery loop.

### D25 — Inbox rows and idempotency claims commit with their effect

Both stage a row and let the caller's single `SaveChanges` commit it alongside the
work. Writing the inbox row in its own transaction meant a later failure left a
row saying the message was handled with no trace of the work, so the redelivery
that should have repaired it was skipped and the effect was lost silently — the
exact outcome an inbox exists to prevent.

### D26 — Audit rows record the request, not the response

`tool_calls` keeps the arguments; `llm_calls` keeps the prompt hash, token counts
and cost. A tool result is already stored as redacted evidence, and a model
completion is the one thing that must not be kept twice. Refusals are audited
like successes, because "the agent asked for the Kubernetes API and was refused"
is the most useful line in a review of the platform's own behaviour.

### D27 — The investigation span is sampled before its parent is consulted

`incident.investigation` is never a root: it starts inside the Kafka consumer
span that triggered it, which continues the trace of the HTTP request that
accepted the alert. Wrapping the sampler in `ParentBasedSampler` meant it was
never consulted at all and the ingress ratio decided everything, so at ten percent
nine investigations in ten vanished.

### D28 — The Temporal workflow returns its terminal status

A `Task`-returning run makes the result an internal BCL type the generated
serialization context cannot name. Every activity succeeded, the incident
resolved, and then the final workflow task failed on the way out and retried
forever, holding the workflow id so a retry of that incident was refused as
already running.

### D29 — `.editorconfig` is a build input

It carries the analyzer severities. Without it in the Docker build context the
container build applies defaults the repository has deliberately turned off and
fails on rules CI never runs.

### D30 — The backend expression lives in the tool artifact

`config/tools/prometheus.query*.yaml` carries a `provider_expressions` block
beside the enum of metrics it offers. A separate metrics-catalogue release read
tidier and would have forced three appsettings files, compose, the ConfigMap, the
Helm values and template and three test hosts to change before anything could
start, because every field on `RegistryOptions` is `[Required]` with
`ValidateOnStart`. The list of permitted metrics already existed as the enum;
two lists of one thing in two files drift.

The objection is that configuration stops being backend-neutral. It never was:
these artifacts already declare `provider: prometheus`.

**Cost:** the enum and its expressions can disagree, so startup validation gained
a seventh check covering coverage, dead expressions, and two tools answering one
selector differently.

### D31 — One escaping routine, shared by PromQL and LogQL

LogQL uses PromQL's label matcher and string literal syntax, so `PromQuery`
serves both. The name reads oddly from Loki's side and that was accepted: two
copies would be two chances to get it wrong, and they would drift the first time
one was fixed.

Two cases refuse rather than escape. A label named `service` or `environment` is
rejected, because the policy engine checks the `service` *argument* against the
execution allowlist and has no reason to look inside `labels` — an agent scoped
to one service could otherwise read another with every authorization check
having passed. An illegal label name is rejected because dropping it would widen
the query rather than narrow it.

### D32 — Observability providers are selected per port, and Production refuses a fake

`Observability:UseFakeProviders` became `MetricsProvider`, `LogProvider` and
`KubernetesProvider`. One switch could not describe the state the platform is
actually in, where metrics and logs have real adapters and Kubernetes does not.

The old key is kept as a nullable property whose only job is to fail startup. A
plain `bool` cannot distinguish unset from `false`, and options binding ignores
keys it does not know, so a deployment that meant "use real backends" would have
silently taken the fake defaults — the exact outcome the original guard existed
to prevent.

The guard now runs in both directions: a real provider with no address is
refused, and so is any fake provider in Production.

**Cost:** `IDatabaseObservability` still has no real adapter, so a Production
deployment that routes a `database.health` call gets nothing. The guard makes
that visible at deploy time rather than in a report.

### D33 — Model identity is a tenant-owned immutable release, not a profile name

`ModelAssignmentRelease` is what an incident pins: a tenant-scoped, versioned,
immutable binding of every model slot to a specific `ModelDeploymentVersion`
behind a specific `ProviderConnectionRevision`. The runtime never looks up a
profile name at request time; the pin carries the full deployment coordinates
and their configuration hashes, and `LlmGateway` audits both the requested and
the observed identity.

Two tenants running the same product on different providers used to require a
profile per environment per tenant and a resolver that hoped they agreed. A
release is a single artifact operators can diff, roll back to and reason about,
and immutability is what makes an incident from last month reproducible today.

**Cost:** two model systems live side by side during the cutover, and the
shipped `config/models` artifacts stop being a runtime resolution path — they
seed the first `ModelAssignmentRelease` at bootstrap and nothing else. Every
registry test that used to assert profile lookup now asserts assignment
resolution instead.

### D34 — The bootstrap seed writes the active pointer directly

`ModelAssignmentBootstrapper` runs after `DatabaseMigrator`, and when a tenant
has no active assignment it inserts the connection, the revision, one
deployment per profile the shipped configuration references, one assignment
release, and the `tenant_active_model_assignments` pointer, in a single
transaction. It skips the operator-facing verify/evaluate/activate gate that a
later `POST /v1/model-assignment-releases/{id}/activate` walks through.

There is no operator at first start. Requiring the two-step gate would make the
compose stack, the integration suite and the eval harness unusable until a
human logged in and clicked, and every one of those environments needs a
working assignment with no operator present.

**Cost:** a wrong `Llm:Bootstrap` endpoint becomes active without a network
round trip to check it. Production compensates: the bootstrapper obtains the
`ProviderRevisionVerificationEntity` row by calling
`ProviderRevisionVerifier.VerifyAsync` and **fails startup** on a failure, so a
misconfigured production process dies at deploy time (`AGENTS.md`).

### D35 — Immutability is a database trigger, not an interceptor

`provider_connection_revisions`, `model_deployment_versions`,
`model_assignment_releases` and `provider_revision_verifications` carry a
`BEFORE UPDATE OR DELETE ... FOR EACH ROW EXECUTE FUNCTION
sre_reject_row_mutation()` trigger; `model_assignment_agent_slots` carries the
same trigger for `UPDATE` only. The function raises
`MODEL_CONFIGURATION_INVALID`, which the application error taxonomy already
maps.

An EF Core `SaveChangesInterceptor` is easier to write, but it only binds a
process that goes through `SreDbContext`. An operator with a psql session, an
ad-hoc migration or a future non-C# consumer sits outside that boundary, and
the whole point of an immutable release is that no such actor can rewrite one.
A trigger binds the row.

**Cost:** raw SQL in a migration for the first time, a `Down` block that must
drop both the trigger and the function, and a new integration test that
`UPDATE`s a revision row and expects the trigger to raise. The application
layer keeps the same code path for the expected-conflict case, so the caller
contract is unchanged.

### D36 — The gateway owns the retry budget; the provider HTTP client has no resilience handler and no timeout

`AddHttpClient(ProviderHttpClients.Chat)` sets `Timeout =
Timeout.InfiniteTimeSpan` and deliberately does not call
`AddStandardResilienceHandler`. `LlmGateway` already owns the retry loop, the
error taxonomy (`ErrorCatalog.IsRetryable`), the audit row and the linked
cancellation token that carries the pinned deployment timeout.

A second retry layer would double-charge a paid provider call without recording
it, and a second, lower `HttpClient` deadline would surface as an
`OperationCanceledException` no adapter's catch block maps to a stable error
code — the caller would see `LLM_PROVIDER_ERROR` for what was actually a
timeout, and the audit row would say the same.

**Cost:** an unbounded `HttpClient` deadline that only the linked cancellation
token closes. If `LlmGateway` ever stops threading that token into
`GetResponseAsync`, an unresponsive provider hangs a request forever; the
regression test that pins the timeout path is what prevents it.

### D37 — The HTTP boundary is proven by two suites, not one

The HTTP boundary is proven across two test projects rather than a single suite.
`tests/SreAiAgent.ApiTests` runs on the default gate with no Docker dependencies,
hosting the API in-process via `WebApplicationFactory<Program>` and substituting
`IEventPublisher`, `IUnitOfWork`, `IModelProviderControlPlane` and
`IMessageProducer` with deterministic in-memory doubles.
`tests/SreAiAgent.IntegrationTests` proves the persisted side effect against real
PostgreSQL (backed by Testcontainers), substituting only `IMessageProducer` and
leaving `IEventPublisher`, `IUnitOfWork` and `IModelProviderControlPlane` in their
production registrations.

Keeping the security, authentication, role authorization and tenant-isolation
boundary assertions in an untraited suite ensures that every default CI run
proves these invariants without requiring a Docker daemon. Tests requiring
database verification (such as confirming that accepted alerts write exactly one
`outbox_messages` row or cross-tenant attempts write none) run under the
`Category=Integration` filter.

**Cost:** two `WebApplicationFactory` fixtures (`SreApiFactory` and
`SreApiPostgresFactory`) and duplicated `TestTokens` and `RepositoryPaths`
helpers across the two test assemblies. This duplication is accepted
deliberately: `TestTokens` depends on `SreAiAgent.Api` (`SreClaims`, `SreRoles`)
and `Microsoft.IdentityModel.JsonWebTokens`, while `RepositoryPaths` makes
assumptions about the repo's filesystem layout. Hoisting them into
`tests/SreAiAgent.TestKit` would add an Api host dependency to a pure kit that
sits strictly at the Domain/Application layer and is consumed by
`SreAiAgent.Application.Tests` and `SreAiAgent.Workflow.Tests`.

### D38 — Adapter vs configuration triage rule (ADP-005)

When integrating a new provider, model, data source or environment, the change
is triaged across four orthogonal architectural axes rather than defaulting to
writing a new adapter:

1. **Configuration or revision:** endpoint URLs, deployment revisions,
   credential references, rate limits and retry budgets belong in configuration
   or an immutable model-provider revision record.
2. **Adapter:** wire protocol, payload format translation, authentication
   handshake mechanics and semantic capability mappings require a new or updated
   adapter implementation.
3. **Scoped product-environment configuration:** label mappings, namespaces, and
   query allowlists are configured per product/environment scope, not hard-coded
   in adapter logic.
4. **Policy and operations decision:** introducing new tool authority, elevated
   permissions, or new data classifications requires an explicit security policy
   and governance decision.

This rule prevents proliferating unnecessary adapter abstractions when existing
adapters can handle variations via immutable configuration revisions, and ensures
that a request for "a new adapter" is answered by which architectural axis
actually changed rather than by the caller.

**Cost:** integration work requires an upfront classification step before code is
written, and requests for bespoke adapters that merely vary endpoints or
credentials are systematically rejected in favor of configuration updates.

### D39 — Alertmanager webhooks are a batch; a partial batch fails whole

`AlertmanagerAlertAdapter.Normalize` read `labels`, `annotations`, `fingerprint`,
`startsAt` and `status` off the payload root. Alertmanager's `webhook_config`
never posts that shape: it always posts the group envelope
(`{"status":...,"alerts":[...],"groupLabels":...,...}`), even for a single
firing alert, so every real delivery failed validation
("requires the 'alertname' and 'service' or 'job' labels") while every test
fixture, shaped like the root-level assumption, passed. D38's triage rule calls
this: the wire format changed nothing about configuration, scope or policy, so
the fix belongs entirely in the adapter, and `IAlertSourceAdapter.Normalize` now
returns `Result<IReadOnlyList<CanonicalAlert>>` so the port can say "these N
alerts", including N = 1 for the other three providers, which are not
batch-shaped and continue to map one payload to one alert.

A batch that is partially valid fails as a whole, and staging is one
`SaveChangesAsync` call for the entire batch rather than one per alert. Both
follow from the same reasoning: Alertmanager retries a `webhook_config`
delivery it never gets a 2xx for, so refusing the whole group on one bad alert,
or on a mid-batch database failure, is recoverable — the next retry sees the
same envelope and can succeed or fail the same way. Silently dropping the one
bad alert, or committing half a batch before a later alert's failure, would
each mean a firing condition this platform was told about never opens an
incident, with nothing in the 202 response saying so.

**Cost:** one malformed alert in an otherwise-healthy Alertmanager group blocks
every alert in that group until the sender fixes it or an operator intervenes;
there is no partial-acceptance path. Grafana's unified alerting can also emit a
batched webhook payload in newer versions; this repository has not confirmed
whether the deployed Grafana version does, so `GrafanaAlertAdapter` still
assumes one alert per payload. Follow-up: verify against a real Grafana
instance before relying on it for high-volume routing.
