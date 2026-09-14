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

### D40 — Product and environment extend tenant's composite key; they are not new first-class entities

`IModelProviderControlPlane`'s 13 members (`ListConnectionsAsync` through
`ActivateAsync`) take only `string tenant`, and every model-provider entity
configuration already threads `Tenant` through the schema as a composite-key
component rather than a plain foreign key to a lookup table:
`ProviderConnectionEntity`, `ProviderConnectionRevisionEntity`,
`ModelDeploymentVersionEntity` and `ModelAssignmentReleaseEntity` each declare
`HasKey(entity => entity.Id)` plus `HasAlternateKey(entity => new {
entity.Tenant, entity.Id })`, and every relationship between them is a
composite foreign key `(Tenant, ForeignId) -> (Tenant, Id)`
(`ModelProviderConfigurations.cs`). `tenant_active_model_assignments`, the
table holding the one currently-active assignment, goes one step further: its
primary key is `Tenant` alone (`TenantActiveModelAssignmentConfiguration.cs`,
`HasKey(entity => entity.Tenant)`), so "exactly one active assignment for X"
is already a schema-enforced invariant, not an application-level check.

Product and environment become two more columns on that same composite key —
`(Tenant, Product, Environment, ...)` wherever `(Tenant, ...)` appears today —
not new entities with their own generated id, table and lifecycle. Rejected: a
`Product` entity keyed `(Tenant, ProductId)`, referenced everywhere through the
same composite-FK shape `ModelProviderConfigurations.cs` already uses. An
earlier draft of this entry rejected that option on isolation grounds — that a
`Products` table would be "a global identity by construction." That claim does
not hold and is retracted, not reused: a table keyed `(Tenant, ProductId)` and
referenced only through `(Tenant, ForeignId) -> (Tenant, Id)` composite
foreign keys is exactly as tenant-owned as a bare column, enforced by the
identical mechanism; nothing about giving Product a surrogate id changes what
a query can reach without a tenant. The real reasons are consistency and
sequencing, not isolation.

Consistency: `Tenant` itself — the platform's outermost and most consequential
scope dimension — has no dedicated entity anywhere in this schema. There is no
`Tenants` table, no `TenantId`, no surrogate key; every entity's `Tenant`
column is a bare `string`, and renaming one (if that were ever needed) would
be exactly the cascading update this decision accepts for `Product`. Giving
`Product`, one level narrower than `Tenant`, more structure than `Tenant`
itself — a surrogate id, a dedicated table, a lifecycle — would be an
inconsistency nothing else in this schema exhibits. `Incident.Environment`
(`IncidentConfiguration.cs`) reinforces the same point from the other
direction: it already lives in the schema as a plain string with no entity, no
foreign key and no lifecycle of its own, and it is never used as a query
filter anywhere in the incident or model-provider repositories.

Sequencing: the entity option's one real advantage — a rename becomes a
single-row update instead of a cascade — only reaches the *live* control-plane
tables, not history, and history is where immutability actually matters.
`PinnedModelCandidate` (`ProviderRuntimeContracts.cs`), the type
`ConfigurationPinner.PinCurrentAsync` freezes into every `ConfigurationSnapshot`,
already denormalizes `Tenant`, `DeploymentLogicalName` and `ProviderModel` as
plain strings at pin time, even though `ModelDeploymentVersionEntity` and
`ProviderConnectionRevisionEntity` *do* have stable surrogate ids of their own.
The established convention here is: keep a stable id for the mutable source of
truth, but copy the human-meaningful value into the snapshot at the moment it
is pinned, so a later rename of the source cannot retroactively change what a
closed incident's report says it ran against. Making `Product` an entity would
not change what gets frozen into a snapshot — it would still be a name, copied
at pin time, exactly like `DeploymentLogicalName` is today — so the entity's
rename benefit never reaches the one place immutability matters, and only pays
for itself on the live side, for an operation (rename) the product-adaptability
guide does not currently plan to expose (a backlog gap, not a design property,
and not the reason for this decision by itself — named only because the cost
is paid today for a benefit nobody exercises yet). A denormalized column is
also the cheaper direction to grow from later: turning a `Product` string into
a real entity, if a genuine need for owned metadata or a stable rename target
appears, is a mechanical backfill — distinct strings become rows, the string
becomes a display name. Collapsing an entity back into a bare column after
code has come to depend on a `ProductId` is not.

Landing on what exists: every `Tenant`-only filter and key in
`ModelProviderRepository` (a dozen-plus call sites) and every
`HasAlternateKey`/`HasForeignKey` pair in `ModelProviderConfigurations.cs`
widens by two columns. `tenant_active_model_assignments.HasKey(entity =>
entity.Tenant)` becomes `HasKey(entity => new { entity.Tenant, entity.Product,
entity.Environment })`, generalizing the same "exactly one active assignment"
invariant to a scope instead of a bare tenant. Rows written before this change
get `Product = ""` and `Environment = ""` (an empty-string sentinel, not
`NULL`, so the composite key and its indexes keep working) rather than a
backfilled guess: nothing about a row written before product/environment
existed tells the truth about which one it belongs to, and inventing one would
be worse than a sentinel that plainly means "not scoped." The JWT gains two
new **optional** claims beside the mandatory `tenant` (`SreClaims.Tenant`);
`PrincipalExtensions` gains `Product()`/`Environment()` reading them with the
same empty-string default, so a caller that sends neither scopes exactly as it
does today — full tenant, no narrowing — and the 13 control-plane members take
a small `TenantScope` value (wrapping `Tenant`, `Product`, `Environment`) in
place of the bare `tenant` string, rather than each growing two more
positional parameters one by one.

Both of the guide's constraints hold under this shape by construction, not by
convention: product and environment only ever appear as trailing columns of a
key whose leading column is `Tenant`, so neither can be queried, indexed or
authorized independent of a tenant, and `Tenant` alone still decides every
existing index that does not opt into the wider scope
(`ix_incidents_active_fingerprint` and the incident table's other indexes stay
exactly as they are — incidents are not in ADP-101's scope).

**Cost:** renaming a product is a cascading update across every table that
carries the string — `provider_connections`, `provider_connection_revisions`,
`model_deployment_versions`, `model_assignment_releases`,
`model_assignment_agent_slots`, `tenant_active_model_assignments` — all inside
one transaction, because nothing gives a product a stable identity independent
of its name. Already-pinned `configuration_snapshots` are correctly left
untouched by that update: they freeze whatever product name was current at pin
time, the same `PinnedModelCandidate` denormalization described above, so a
report opened against an old snapshot can legitimately show a product name
that no longer exists live — intended immutable-history behavior, not a
defect this decision needs to fix. There is nowhere to hang product-specific
metadata (a display name distinct from the key, an owner, a description)
without a later migration to an actual entity if that need ever arrives.

`TenantScope` is a closed set of exactly three scope kinds, not three free
strings, and an illegal combination is a construction error the type refuses
to represent, not a row a repository or a migration has to reject later:

1. **TenantWide** — product and environment both absent. Applies to the whole
   tenant; nothing narrower has been asked of it.
2. **ProductWide** — product present, environment absent. Applies to every
   environment of that product.
3. **Full** — product and environment both present. Applies to exactly that
   product in exactly that environment.

Environment present with product absent (an environment with no product) is
the one combination this set has no member for, and `TenantScope`'s
constructor rejects it outright rather than letting it reach a repository,
let alone a column. This also answers the question the first draft of this
entry left open: environment never sits beside product as an independent
dimension; it exists only inside one, so two products in the same tenant each
naming a "production" environment are two unrelated `Full` scopes by
construction, not two callers sharing one name.

A row written before this decision and a deliberately tenant-wide assignment
created afterward are the same `TenantScope` kind, `TenantWide`, not two
meanings hiding behind one representation: operationally, a row nobody has
ever narrowed behaves identically whether that is because product did not
exist yet or because an operator chose not to narrow it. Telling those two
histories apart — which rows were never reviewed for a migration to
`ProductWide`/`Full` versus which were reviewed and left broad on purpose — is
an audit question for whoever runs a tenant's migration, not a scope-model
question, and it does not get a member in this closed set.

**Authorization matrix.** This governs who may create, read, update or
activate a row of a given scope through the control plane; it says nothing
about whether an investigation's runtime lookup of the active assignment
falls back from `Full` to `ProductWide` to `TenantWide` when no exact match is
activated — that is a separate question for whichever of F1-5/ADP-103 cuts
over `IModelProviderControlPlane`'s 13 members, and this decision does not
answer it. For authorization, ADP-203's rule that scope never comes from the
request body applies here without exception: a caller's `TenantScope` is
derived only from validated claims, and it must equal a row's `TenantScope`
exactly — kind and every value — for either read or write. A broader or
narrower claim set than a row's kind never matches it, in either direction:

| Row's scope | Claims required to read or write it |
|---|---|
| `TenantWide(tenant)` | `tenant` claim equal; no `product` claim present |
| `ProductWide(tenant, product)` | `tenant` claim equal and `product` claim equal; no `environment` claim present |
| `Full(tenant, product, environment)` | `tenant`, `product` and `environment` claims all equal |

A missing `product` or `environment` claim is never a wildcard. It derives
`TenantScope.TenantWide` for that caller, a specific and narrow scope that
matches only other `TenantWide` rows — not "every row in this tenant" and not
"every row this tenant has not yet narrowed for someone else." Symmetrically,
a caller holding `product`/`environment` claims gets no special reach into
`TenantWide` rows either: `Full` and `TenantWide` are different kinds, and
different kinds never match regardless of which one looks "more specific."

Named as a real cost, not smoothed over: once a tenant has rows in more than
one `TenantScope` kind, a caller whose token is never reissued with
`product`/`environment` claims can still reach only that tenant's `TenantWide`
rows. If the tenant's real assignment has since moved to `ProductWide` or
`Full`, that caller's old row is stranded — reachable by nobody until
something reissues its token with the right claims or an explicit
control-plane operation promotes or copies the row into the new scope. No
query answers "the tenant's assignment" without first deciding which kind is
meant, and this decision does not invent a fallback that would quietly answer
it for them.

F1-3 exists to prove this matrix, not merely to exercise the code path, so its
cases are named here rather than left to whoever implements it to guess:

- Cross-tenant is refused: tenant A's claims never read or write tenant B's
  row, at any scope kind.
- Same tenant, different product is refused: a `ProductWide`/`Full` caller for
  product P never reads or writes a row scoped to product Q in the same
  tenant.
- A claim-less caller does not read another product's rows: a caller with
  only a `tenant` claim (`TenantScope.TenantWide`) is refused on every
  `ProductWide` and `Full` row in that tenant, not merely on other tenants'
  rows.
- An illegal scope combination is rejected at construction: building a scope
  from an `environment` claim with no `product` claim fails before any
  repository call, with a test asserting the rejection happens at
  `TenantScope` construction and never reaches `ModelProviderRepository`.
