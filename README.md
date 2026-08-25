# SRE AI Agent Platform

Evidence-first incident investigation platform on .NET 10. An alert arrives from
Alertmanager, Datadog, Grafana, a generic webhook or Kafka; the platform opens an
incident, pins an immutable configuration snapshot, routes the incident to a set
of read-only expert agents, gathers evidence through a policy-enforced tool
gateway, correlates the findings and persists a root cause report.

The full design, including every decision this repository implements, is in
[ARCHITECTURE.md](ARCHITECTURE.md). Implementation decisions and their rationale
are recorded in [docs/DECISIONS.md](docs/DECISIONS.md).

## Layout

| Path | Purpose |
|---|---|
| `src/SreAiAgent.Domain` | Aggregates, value objects and invariants. No infrastructure dependencies. |
| `src/SreAiAgent.Contracts` | Wire contracts for the HTTP and messaging boundaries. |
| `src/SreAiAgent.Application` | Use cases, ports, routing policy, budgets and diagnostics definitions. |
| `src/SreAiAgent.Infrastructure` | Adapters: PostgreSQL, Kafka, LLM, observability backends, registries, security. |
| `src/SreAiAgent.Workflow` | Durable workflow adapters (Temporal, plus a development-only inline executor). |
| `src/SreAiAgent.Api` | ASP.NET Core minimal API host. |
| `src/SreAiAgent.AlertWorker` | Alert ingestion worker. |
| `src/SreAiAgent.InvestigationWorker` | Investigation worker and workflow worker host. |
| `tests/` | Unit, contract, architecture, security, integration and evaluation suites. |
| `prompts/`, `config/` | Versioned prompt, model, agent, tool, flag and policy artifacts. |
| `deploy/` | Dockerfile, compose stack, Helm chart and Kubernetes manifests. |

The dependency rule is enforced, not documented: `Directory.Build.targets` fails
the build if Domain, Contracts or Application declare an infrastructure package,
and `SreAiAgent.ArchitectureTests` re-asserts the same rule against the compiled
assemblies.

## Prerequisites

- .NET SDK 10.0.302 or a later 10.0.3xx feature band (pinned in `global.json`)
- Docker with Linux containers, for the compose stack and the integration suite

## Everyday commands

```bash
dotnet build SreAiAgent.slnx
dotnet test  SreAiAgent.slnx
eng/ci.sh                      # the full CI gate
eng/ci.sh --with-integration   # plus Testcontainers-backed integration tests
```

`dotnet restore --locked-mode` is part of the gate, so adding or changing a
package requires regenerating the lock files with
`dotnet restore SreAiAgent.slnx --force-evaluate`.

## Quality baseline

Central package management, lock files, nullable reference types, warnings as
errors, .NET analyzers at `Recommended`, Meziantou analyzers, a banned-API list
and enforced code style. See `Directory.Build.props`, `Directory.Packages.props`,
`BannedSymbols.txt` and `.editorconfig`.
