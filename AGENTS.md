# Working in this repository

Guidance for coding agents. `CLAUDE.md`, `.github/copilot-instructions.md` and
`.gemini/settings.json` all point at this file. It is the only source of truth:
keep those three as pointers rather than letting them grow a second opinion.

Evidence-first incident investigation platform on .NET 10: an alert arrives, the
platform opens an incident, pins an immutable configuration snapshot, routes it to
read-only expert agents, gathers evidence through a policy-enforced tool gateway
and persists a root cause report. `README.md` has the layout.

## The one rule

**Compiling is not working.** About twenty defects in this repository's history
compiled cleanly, passed unit tests, and were found only by running the process
against real infrastructure. Several were silent: effects lost to a prematurely
committed inbox row, deduplication lost to the wrong clock, investigations
invisible in Jaeger, a workflow that could never close.

So: run the gate, and if you touched anything that crosses a process boundary,
run the stack.

```bash
bash eng/ci.sh                     # the gate: restore --locked-mode, format, build
                                   # -warnaserror, tests, vulnerable package audit
bash eng/ci.sh --with-integration  # adds Testcontainers suites; needs Docker
```

On Windows, where there may be no bash, `eng/ci.ps1` runs the same steps in the
same order:

```powershell
powershell -ExecutionPolicy Bypass -File eng/ci.ps1     # Windows PowerShell 5.1
pwsh -File eng/ci.ps1 -WithIntegration                  # PowerShell 7
```

`.github/workflows/ci.yml` runs these same scripts on every pull request — the
Linux gate with integration, the Windows gate, and the Helm render check — so a
step belongs in the scripts, never only in the workflow.

Stack procedure: `docs/agents/recipes/verify-end-to-end.md`.

## Rules the build enforces

Do not work around these. The analyzer wins, and its message names the remedy.

| Use | Not | Enforced by |
|---|---|---|
| `timeProvider.GetUtcNow()` | `DateTimeOffset.UtcNow`, `DateTime.Now` | RS0030 |
| `activity.SetSafeTag(...)` | `Activity.SetTag` / `AddTag` | RS0030, keeps prompts and tokens off spans |
| `JsonTypeInfo` overloads | reflection `JsonSerializer.Serialize<T>(x)` | RS0030, D10 |
| `IHttpClientFactory` | `new HttpClient()` | RS0030 |
| `await` | `.Result`, `.Wait()`, `Thread.Sleep` | RS0030 |
| `[LoggerMessage]` | interpolated or boxed logging | CA1848, CA2254 as errors |
| ports in Application | infrastructure packages in Domain/Application/Contracts | SREARCH001 and `SreAiAgent.ArchitectureTests` |
| `Result<T>` + `ErrorCode` | exceptions for expected failures | D06; invariants still throw `DomainException` |

Warnings are errors everywhere, so these break the build too: `IDE0005` unused
using, `CA1859` interface where a concrete type would do, `CA1822` member that
could be static, `MA0045` synchronous IO, `MA0032`/`MA0040` a missing
`CancellationToken`.

## Rules nothing enforces — these fail silently

Each of these has already cost a debugging session. Nothing will tell you.

- **A new agent without an entry in `config/flags/agent-flags.v1.yaml`** loads,
  passes startup validation, and never runs. The file's `defaults` are
  `state: disabled`, and `RegistryValidator` does not check flag coverage.
  `ShippedConfigurationTests` does.
- **A new agent or tool the scripted model provider does not know about** runs and
  produces nothing. `ScriptedChatClient` branches on prompt content and tool names;
  it is the only `IChatClient` in V1.
- **A span attribute missing from `TraceAttributes`** is dropped. Only
  `sre_trace_attribute_rejected_total` moves.
- **A type crossing Temporal but missing from `WorkflowPayloadJson`** fails the
  workflow *after* it has started, and the workflow id stays held.
- **Optional members on a type with `required` members must be nullable.**
  `System.Text.Json` does not run property initializers on such types, so a
  defaulted member silently arrives as `null` or `0`.

## Where to look

Do not read `ARCHITECTURE.md` end to end — it is 1,800 lines. Go by section.

| Question | File |
|---|---|
| How do I do this specific task? | `docs/agents/recipes/` — start here |
| Where does this live, which of the six JSON contexts? | `docs/agents/MAP.md` |
| Why is it built this way? | `docs/DECISIONS.md`, D01–D32 |
| What has bitten us before? | `docs/agents/PITFALLS.md` |
| It is broken and I am operating it | `docs/RUNBOOK.md` |
| What is the intended design? | `ARCHITECTURE.md`, by section number |

Recipes, in rough order of how often they are needed:

| Task | Recipe |
|---|---|
| Add an expert agent | `docs/agents/recipes/add-an-expert-agent.md` |
| Add a tool | `docs/agents/recipes/add-a-tool.md` |
| Change a serialized type | `docs/agents/recipes/change-a-serialized-type.md` |
| Add or update a package | `docs/agents/recipes/add-a-package.md` |
| Add a span attribute or metric | `docs/agents/recipes/add-telemetry.md` |
| Prove a change works | `docs/agents/recipes/verify-end-to-end.md` |

## Conventions

**Commits.** Conventional Commits, imperative mood, describing the behaviour that
changed rather than the files touched. The body carries the reasoning; several
existing commits are worth reading before writing one.

**Comments explain why, never what.** The density and tone in this repository are
deliberate — a comment records the decision or the trap, not the mechanics. Match
the file you are editing.

**Visibility.** Infrastructure types are `internal`; test projects see them
through `InternalsVisibleTo`. Do not widen a type to make a test compile.

**Tests belong with the layer they cover.** A new suite needs an entry in
`SreAiAgent.slnx` and regenerated lock files. Never create an empty test project:
the test platform treats "zero tests ran" as failure (exit 8), in both directions
— a trait filter that matches nothing fails just as loudly.

**Never hand-edit `packages.lock.json`.** See `docs/agents/recipes/add-a-package.md`.

**Adding a decision.** If you make a call that a future reader would question,
append it to `docs/DECISIONS.md` with its reason and its cost. A decision without
its cost is not one anybody can revisit.

**Plans live in the repository.** Multi-session work gets a file in
`docs/plans/` with a status block, committed before the first code change and
updated as steps land. A plan that only exists in a chat session is lost with
the session; this has already cost one reconstruction.
