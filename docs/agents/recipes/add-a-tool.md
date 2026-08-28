# Add a tool

<!-- example-paths: prometheus.labels PrometheusLabels -->

A tool is the only way an agent touches a real system, so it is half artifact and
half code: a definition that can be released without a deploy, and an adapter that
cannot.

**V1 ships read-only tools only.** There is no `IWriteToolRegistry` implementation
registered, so a component asking for write capability cannot be constructed. Do
not add one without reading ARCHITECTURE.md 15.2 and 20 first; it is a V2 change
that also requires the approval flow.

## Files to touch, in order

Using `prometheus.labels` as the example.

**1. `config/tools/prometheus.labels.v1.yaml`** — the definition.

```yaml
name: prometheus.labels
version: v1
risk: read_only
provider: prometheus
description: >
  What a model reads to decide whether to call this. Write it for that reader.
required_scopes: [ metrics:read ]
timeout_seconds: 10
max_result_bytes: 65536
parameters:
  type: object
  additionalProperties: false
  required: [ metric ]
  properties:
    metric: { type: string, maxLength: 128 }
```

Constrain the parameters as tightly as the backend allows — an `enum` where the
set is known, `maxLength` on every string, `additionalProperties: false` always.
The schema is the authorization boundary for arguments a model produced.

Numeric constraints must stay numeric. YAML scalars arrive as strings, and a
`maximum: 360` that survives as `"360"` is applied by no JSON Schema
implementation, so the bound silently stops existing.
`ShippedConfigurationTests` asserts this.

**2. `config/tools/releases/tools-<date>.yaml`** — pin the version.

**3. `src/SreAiAgent.Infrastructure/Tools/Adapters/`** — implement `IToolAdapter`.

```csharp
internal sealed class PrometheusLabelsAdapter(IMetricsProvider metrics) : IToolAdapter
{
    public string ToolName => "prometheus.labels";

    public async Task<ToolAdapterResult> ExecuteAsync(
        JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    { ... }
}
```

The adapter is reached only after the gateway has run the whole policy pipeline,
so arguments are trustworthy. The reverse also holds: **never call an adapter from
anywhere but the gateway** — doing so skips authorization, the risk ceiling, rate
limiting, the result cap, redaction and the audit record in one step.

Return a real `Summary`. It is what the agent reads first and what lands on the
evidence row; an adapter returning raw JSON with no summary forces the model to
work out the shape of a payload before it can reason about the incident. Set
`IsPartial` when the backend answered from incomplete data, or an agent will read
"no errors" from a partial scrape.

**4. Narrow the result** in `Tools/Adapters/ToolResultEnvelopes.cs` and add the
envelope type to the context in that file. The result has to survive the size cap,
pass through redaction and then fit in a prompt alongside every other piece of
evidence. Downsample here, where the shape is understood, rather than leaving a
byte-count truncation to cut a JSON document in half.

**5. `ToolsDependencyInjection.AddSreTools`** — register the adapter.

```csharp
services.AddSingleton<IToolAdapter, PrometheusLabelsAdapter>();
```

Registered one by one on purpose, not by assembly scanning: which components can
reach a production system should be visible in a diff.

**6. `config/agents/*.yaml`** — grant it to the experts that should have it.

An allowlist. A tool added to the registry today is unavailable to every agent
until someone grants it deliberately.

**7. `ScriptedChatClient.cs`** — make the fake model request it, if you want the
tool exercised end to end.

## What happens if you skip one

| Skipped | Result |
|---|---|
| Tool release entry | An agent allowing it fails startup validation. Loud. |
| Adapter or its registration | `TOOL_NOT_FOUND` at call time; the run continues without that evidence |
| Agent allowlist | `TOOL_POLICY_DENIED`, audited. The gateway working as designed |
| Result envelope | The raw backend payload competes with evidence for prompt space |
| **ScriptedChatClient** | **The tool is never called, and nothing says so** |

## Verify

```bash
bash eng/ci.sh
```

`Infrastructure.Tests/ToolGatewayTests` exercises the gateway against the shipped
config; `SecurityTests/ToolSecurityTests` asserts every shipped tool is
`RiskLevel.ReadOnly` and that no tool schema exposes `command`, `script`, `shell`,
`sql` or `exec`. Add a case to `ToolGatewayTests` for the new argument shape,
including one that the schema must reject.

Then `docs/agents/recipes/verify-end-to-end.md`, and check `tool_calls` for the
new row with `outcome` and a non-null `arguments`.

## Related

- `docs/DECISIONS.md` D15 (registries physically separated), D26 (audit records the request)
- `ARCHITECTURE.md` 6.2 for the thirteen-step pipeline, 15.2, 20
