# Add an expert agent

<!-- example-paths: network-expert network_expert -->

Adding an expert is a **configuration release, not a deploy**. That is the whole
point of ARCHITECTURE.md 7.1 and 8.2: a new expert can be rolled out, scoped to an
environment and killed without shipping code.

The cost of that design is that one conceptual change spans six files, and two of
the ways to get it wrong produce no error at all.

## Files to touch, in order

Using `network-expert` as the example.

**1. `prompts/network-expert/investigate.yaml`** — the prompt body.

The prompt's identity is the `name:` field *inside* the file, not the folder. The
folder is only used for discovery. Existing prompts use an underscore in the name
and a hyphen in the folder, which is easy to mistype:

```yaml
name: network_expert.investigate     # <- the identity
version: v1
max_rendered_characters: 32000
required_variables: [ ... ]
```

Both `system:` and `user:` are required, and `max_rendered_characters` must be
positive — an unbounded prompt is a cost incident waiting to happen. Compose the
shared policy fragments (`{{shared.system-policy}}` and friends) the way the
existing expert prompts do; a prompt that omits `shared.security-policy` loses the
entire untrusted-data policy.

**2. `prompts/releases/sre-prompts-<date>.yaml`** — pin the version.

```yaml
prompts:
  network_expert.investigate: v1
```

There is no `latest` and no API to request one. Creating a *new* release means
copying the file and bumping its name, never editing an old one: an incident from
last month has to stay reproducible.

**3. `config/agents/network-expert.v1.yaml`** — the agent itself.

Copy `database-expert.v1.yaml`. `prompt_ref` and `model_profile` are
`name:version` references resolved through the pinned releases. `tools.allow` is
an allowlist, never a denylist. `output_schema` must be `AgentFinding`; it is the
only one implemented.

Budgets are hard stops, not targets. `RegistryValidator` rejects a non-positive
budget, a `max_llm_calls` above twice `max_iterations`, and any budget that
exceeds the per-incident ceiling in `config/policies/`.

**4. `config/agents/releases/agents-<date>.yaml`** — pin the agent version.

**5. `config/flags/agent-flags.v1.yaml`** — availability and routing.

```yaml
  - name: network-expert
    version: v1
    availability:
      state: active
      environments: { production: true, staging: true }
    routing:
      allowed_incident_types: [ network, latency, timeout ]
    execution:
      mode: read_only
    rollout:
      mode: all
```

**6. `src/SreAiAgent.Infrastructure/Llm/ScriptedChatClient.cs`** — teach the fake
model about the new expert.

V1 ships no real model provider; `ScriptedChatClient` is the only `IChatClient`.
It branches on prompt content and on the tool names it finds in the prompt. If it
does not recognise anything your agent offers, the agent runs, spends its budget
and returns nothing.

## What happens if you skip one

| Skipped | Result |
|---|---|
| Prompt file or prompt release | Host refuses to start. `RegistryValidator` names the missing reference. Loud, cheap. |
| Agent release entry | Agent is on disk and invisible. No error: the registry only loads what a release pins. |
| Model profile that supports structured output | Host refuses to start. |
| A tool in `allow` that no release pins | Host refuses to start. |
| **Flags entry** | **Agent loads, startup validation passes, agent never runs.** `defaults` is `state: disabled` and `RegistryValidator` does not check flag coverage. `ShippedConfigurationTests` does. |
| **ScriptedChatClient** | **Agent runs, produces no finding, no error anywhere.** The report says no expert produced a verifiable finding. `ShippedConfigurationTests` catches an agent it cannot exercise at all. |

The two bold rows are why this recipe exists.

## Verify

```bash
bash eng/ci.sh
```

`Infrastructure.Tests/ShippedConfigurationTests` runs the real startup path
against the real artifacts, so a broken cross-reference fails here rather than on
deploy. It also catches the two failures nothing else reports: a missing flag
entry, and an agent the scripted provider cannot exercise.

Then run it for real — a config-only change still needs the pipeline to prove the
agent contributes evidence:

```bash
# docs/agents/recipes/verify-end-to-end.md has the full procedure
docker compose -f deploy/docker/compose.yaml up -d --build
```

Send an alert whose labels route to the new incident type and confirm the agent
appears in `agent_runs` with a non-zero `llm_call_count`, and in the report's
`contributing_agents`.

## Related

- `docs/DECISIONS.md` D09 (one scripted `IChatClient`), D12 (registries frozen at startup)
- `ARCHITECTURE.md` 7.1, 8.2, 11.3, 24
