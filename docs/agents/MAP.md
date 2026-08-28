# Map

Where things live, and which of the near-identical options is the right one.

Written for the questions that cost the most time to answer by reading: *which*
JSON context, *which* composition root, *which* test suite, *where* a rule is
actually enforced. Start at `AGENTS.md`; come here when you know what you want to
change but not where it is.

---

## Projects

Dependencies point inward. `Directory.Build.targets` fails the build
(`SREARCH001`) if the three pure layers declare an infrastructure package, and
`SreAiAgent.ArchitectureTests` re-asserts it against compiled assemblies, which
also catches what arrives through a project reference.

| Project | Holds | May reference |
|---|---|---|
| `SreAiAgent.Domain` | Aggregates, value objects, invariants, domain events | nothing |
| `SreAiAgent.Contracts` | HTTP and messaging wire types, their JSON context | nothing |
| `SreAiAgent.Application` | Ports, use cases, agent runtime, routing, budgets, diagnostics definitions | Domain, Contracts |
| `SreAiAgent.Infrastructure` | Every adapter: PostgreSQL, Kafka, LLM, tools, registries, security, telemetry | all of the above |
| `SreAiAgent.Workflow` | Temporal adapter and the development inline executor | Application, Domain, Contracts |
| `SreAiAgent.Api` | Minimal API host | all |
| `SreAiAgent.AlertWorker` | Alert ingress and ingestion host | all except Workflow |
| `SreAiAgent.InvestigationWorker` | Investigation consumer and Temporal worker host | all |

`SreAiAgent.Workflow` deliberately does **not** reference Infrastructure — the
workflow talks to `IInvestigationActivities`, and the architecture suite asserts
it. The two worker hosts do not reference each other.

Infrastructure types are `internal`. Test projects see them through
`InternalsVisibleTo`; widening a type to make a test compile is the wrong fix.

---

## The five JSON contexts

Reflection-based serialization is banned (D10), so every serialized type belongs
to exactly one generated context. Picking the wrong one produces a runtime
failure that names a type but not a remedy.

| Context | File | Covers |
|---|---|---|
| `SreAiAgentJsonContext` | `Contracts/Serialization/` | Everything crossing HTTP or Kafka — requests, responses, event envelopes, `CanonicalAlert` |
| `LlmJsonContext` | `Infrastructure/Llm/` | Model request and response shapes: `AgentTurn`, findings, classification, correlation |
| `WorkflowPayloadJson` | `Workflow/Temporal/` | Every activity argument and result, because Temporal writes them into workflow history |
| `JsonbConverters` | `Infrastructure/Persistence/` | Types stored in a `jsonb` column |
| `ToolResultEnvelopes` | `Infrastructure/Tools/Adapters/` | The narrowed JSON an agent sees for a tool result |

A type can need more than one. An activity result that is also persisted as
`jsonb` and also returned over HTTP belongs in three.
`docs/agents/recipes/change-a-serialized-type.md` is the procedure.

---

## Where each rule is enforced

Knowing this saves arguing with a diagnostic.

| Rule | Enforced at | By |
|---|---|---|
| Layer dependencies | build and test | `Directory.Build.targets` (`SREARCH001`), `ArchitectureTests` |
| Banned APIs | build | `BannedSymbols.txt`, `RS0030` |
| Analyzer severities | build | `.editorconfig` — a build input, also copied into the Docker context |
| Span attribute allowlist | runtime, silently | `Application/Diagnostics/TraceAttributes.cs` |
| Config cross-references | host startup and test | `Infrastructure/Registries/RegistryValidator.cs`, `Infrastructure.Tests/ShippedConfigurationTests.cs` |
| Flag coverage and scripted-provider reach | test | `Infrastructure.Tests/ShippedConfigurationTests.cs` |
| Read-only tools only | container build | no `IWriteToolRegistry` implementation is registered |
| No prompts or tokens on spans | test | `SecurityTests/SpanAttributeLeakTests.cs` |
| Package versions | restore | CPM plus `packages.lock.json` under `--locked-mode` |

`RegistryValidator` covers the six checks of ARCHITECTURE.md 7.1. It does **not**
check that an agent has a feature flag entry, nor that the scripted model provider
knows how to exercise it; `ShippedConfigurationTests` covers both. See the recipe.

---

## Composition roots

Each host wires its own set. An extension registered in the wrong host is how a
container fails to build, or worse, how a host quietly acquires a capability.

| Call | Registers | File |
|---|---|---|
| `AddSreConfiguration` | Options binding and `ValidateOnStart` | `Infrastructure/DependencyInjection.cs` |
| `AddSreRegistries` | Prompt, model, agent, tool, flag, policy registries and the startup validator | same |
| `AddSreLlm` | `LlmGateway` and the scripted `IChatClient` | same |
| `AddSreTools` | Tool gateway, policy engine, rate limiter, adapters | `ToolsDependencyInjection.cs` |
| `AddSreObservability` | Observability providers; refuses non-fake providers in V1 | same |
| `AddSrePersistence` | Pooled `SreDbContext`, repositories, queries, outbox and inbox stores | `PersistenceDependencyInjection.cs` |
| `AddSreOutboxDispatcher` | The dispatcher background service | `MessagingDependencyInjection.cs` |
| `AddSreAlertIngestion` | The four alert source adapters and the normalizer | same |
| `AddSreAlertConsumer` / `AddSreInvestigationConsumer` | Kafka consumers | same |
| `AddSreTelemetry` | OpenTelemetry tracing, metrics and log correlation | `Observability/TelemetryDependencyInjection.cs` |
| `AddSreApplication` | Handlers with no workflow dependency | `Application/ApplicationDependencyInjection.cs` |
| `AddSreInvestigationUseCases` | Handlers that need `IInvestigationWorkflow` — investigation worker and API only | same |
| `AddSreWorkflow` / `AddSreWorkflowWorker` | The workflow engine and, for Temporal, its worker | `Workflow/WorkflowDependencyInjection.cs` |
| `AddSreSecurity` | JWT bearer, five role policies, the production guard | `Api/Security/SecurityDependencyInjection.cs` |

`AddSreApplication` and `AddSreInvestigationUseCases` are split because the alert
worker has no workflow engine, and a handler asking for one would stop that host
from starting.

---

## Configuration artifacts

Nothing here is code, and that is the point: adding an expert is a release, not a
deploy. Every reference is resolved through a **pinned release**, never by
`latest`, and the resolved set is frozen into the incident's configuration
snapshot at creation.

| Path | Contents |
|---|---|
| `config/agents/*.v1.yaml` | One expert each: prompt ref, model profile, tool allowlist, budgets |
| `config/tools/*.v1.yaml` | Tool definitions and their JSON Schema parameters |
| `config/models/*.v1.yaml` | Model profiles and capabilities |
| `config/flags/agent-flags.v1.yaml` | Availability, routing, execution mode, rollout |
| `config/policies/policies-*.yaml` | Per-incident ceilings and the timeout hierarchy |
| `prompts/**/**.yaml` | Prompt bodies, including the shared policy fragments |
| `*/releases/*.yaml` | The immutable manifests that pin a version for every entry |

Old release files stay. An incident from last month has to remain reproducible.

---

## Test suites

| Suite | Owns |
|---|---|
| `Domain.Tests` | Invariants, state transitions, fingerprints, confidence bounds |
| `Contracts.Tests` | Wire format: names, casing, absent members |
| `Application.Tests` | Routing, budgets, finding binding, diagnostics |
| `Infrastructure.Tests` | Adapters, redaction, prompt rendering, sampling, and the configuration that ships |
| `Workflow.Tests` | The shared investigation sequence and Temporal payload round trips |
| `ArchitectureTests` | Assembly references |
| `SecurityTests` | Span leakage, write-tool denial, prompt injection, the production guard |
| `IntegrationTests` | Anything needing real PostgreSQL; every suite carries `Category=Integration` |

The default gate excludes `Category=Integration`. A suite with no matching tests
fails the run (exit 8), which is why `IntegrationTests` carries one untraited
convention test and why the integration step is scoped to that one project.
