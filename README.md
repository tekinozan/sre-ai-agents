# SRE AI Agent Platform

Evidence-first incident investigation platform on .NET 10. An alert arrives from
Alertmanager, Datadog, Grafana, a generic webhook or Kafka; the platform opens an
incident, pins an immutable configuration snapshot, routes the incident to a set
of read-only expert agents, gathers evidence through a policy-enforced tool
gateway, correlates the findings and persists a root cause report.

The full design, including every decision this repository implements, is in
[ARCHITECTURE.md](ARCHITECTURE.md). Implementation decisions and their rationale
are recorded in [docs/DECISIONS.md](docs/DECISIONS.md).

If you are a coding agent, or directing one, start at [AGENTS.md](AGENTS.md): it
carries the rules the build enforces, the ones that fail silently, and pointers to
the task recipes in [docs/agents/](docs/agents/).

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

## Local Compose stack

The local stack uses one PostgreSQL 17 service and its retained named
`postgres-data` volume. Its `postgres-provision` job creates the logical
`sre`, `temporal`, and `temporal_visibility` databases on a fresh volume.
`businessowner` owns the business database and `temporal` owns both Temporal
databases; `sre_admin` exists only to bootstrap a new local volume. The stack
keeps its scripted model provider and fake observability adapters by default.

All published ports bind to `127.0.0.1` by default. Set `SRE_BIND_ADDRESS=0.0.0.0`
only when another machine must reach the stack. When running alongside another
local stack, every published host port is overrideable to prevent collisions:
`SRE_API_PORT` (8080), `SRE_POSTGRES_PORT` (55432), `SRE_KAFKA_PORT` (19092),
`SRE_TEMPORAL_PORT` (7233), `SRE_TEMPORAL_UI_PORT` (8088), `SRE_JAEGER_PORT` (16686),
`SRE_OTEL_PORT` (4317), and under the observability overlay `SRE_PUSHGATEWAY_PORT` (9091),
`SRE_PROMETHEUS_PORT` (9090), and `SRE_LOKI_PORT` (3100).

A retained volume from the older two-PostgreSQL layout is provisioned additively.
To safely validate an existing legacy volume without risking local data, clone the
source volume into the isolated project's exact named volume (`sre-legacy_postgres-data`).
The command mounts the source read-only and refuses to overwrite an existing target:

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

Supply the legacy administrator explicitly, and keep the application on the
existing business owner while it holds the legacy schema ownership:

```powershell
$env:POSTGRES_LEGACY_ADMIN_USER = "sre"
$env:POSTGRES_LEGACY_ADMIN_PASSWORD = "<existing password>"
$env:POSTGRES_BUSINESS_USER = "sre"
$env:POSTGRES_BUSINESS_PASSWORD = "<existing password>"
docker compose -p sre-legacy -f deploy/docker/compose.yaml up -d --build
```
The provisioner never drops a database or volume, resets a password, changes an
existing role's attributes, or transfers database ownership. It creates only
missing roles and databases. New local credentials are convenience defaults, not
a production credential design: production uses externally managed, distinct
migration and runtime credentials with reviewed ownership and grants, and runs
Temporal without the local auto-setup bootstrap.

## Everyday commands

```bash
dotnet build SreAiAgent.slnx
dotnet test  SreAiAgent.slnx
bash eng/ci.sh                 # the full CI gate
bash eng/ci.sh --with-integration   # plus Testcontainers-backed integration tests
```

On Windows without bash, `eng/ci.ps1` runs the same steps in the same order:

```powershell
powershell -ExecutionPolicy Bypass -File eng/ci.ps1
```

`dotnet restore --locked-mode` is part of the gate, so adding or changing a
package requires regenerating the lock files with
`dotnet restore SreAiAgent.slnx --force-evaluate`.

## Quality baseline

Central package management, lock files, nullable reference types, warnings as
errors, .NET analyzers at `Recommended`, Meziantou analyzers, a banned-API list
and enforced code style. See `Directory.Build.props`, `Directory.Packages.props`,
`BannedSymbols.txt` and `.editorconfig`.
