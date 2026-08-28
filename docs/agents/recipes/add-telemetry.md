# Add a span attribute or a metric

Tracing is a first-class requirement here, not decoration: an investigation that
cannot be explained afterwards is not much better than no investigation. It is
also the one subsystem where a mistake is silent by design.

Everything lives in `src/SreAiAgent.Application/Diagnostics/`:

| File | Holds |
|---|---|
| `SreActivitySources.cs` | The six sources: Investigation, Agent, Llm, Tool, Messaging, Persistence |
| `SreSpanNames.cs` | Span names, matching ARCHITECTURE.md 16.1 exactly |
| `TraceAttributes.cs` | The allowlist and `SetSafeTag` |
| `SreMetrics.cs` | The sixteen instruments of 16.2, created once |

`System.Diagnostics` is a BCL primitive, so instruments live with the code that
records them; the OpenTelemetry SDK wiring stays in one Infrastructure file (D13).
Code that records a span has no idea whether anything is listening.

## Adding a span attribute

**1. Decide whether it is allowed to exist.** ARCHITECTURE.md 16.1 forbids
prompts, tokens, credentials and raw tool results on spans. A tracing backend is a
different trust domain: more readers, longer retention, wider export. Counts are
fine, contents are not — `tool.call.count` yes, the tool output no. If you need to
identify a prompt, use its hash; `prompt.hash` exists for that.

**2. Add the constant and put it in the allowlist** in `TraceAttributes.cs`. Both.
The `Allowed` frozen set is separate from the constants, and a constant that is
not in the set is dropped at runtime.

**3. Set it through `SetSafeTag`.**

```csharp
activity.SetSafeTag(TraceAttributes.YourNewKey, value);
```

`Activity.SetTag` and `AddTag` are banned (`RS0030`). There is one sanctioned call
site, inside `SetSafeTag`, behind a scoped `#pragma`.

`SetSafeTag` is an extension on `Activity?` and returns early when the activity is
null, so with no exporter attached neither the allowlist lookup nor the boxing
happens. Call it without a null check.

**If you skip step 2 the tag is dropped and nothing fails.** The only signal is
`sre_trace_attribute_rejected_total` moving off zero, which is why that counter
exists — see `docs/RUNBOOK.md`.

## Adding a metric

Add a field, create it in the constructor, expose a recording method.
`SreMetrics` takes an `IMeterFactory` so tests can collect from an isolated meter
instead of racing every other test in the process.

Instruments are fields, not resolved per call: creating one is a dictionary lookup
plus a lock inside the meter, and these are recorded on the hottest paths in the
system.

Use `TagList` for dimensions — it is a struct, and the alternative allocates a
dictionary per recording. Keep cardinality bounded: tenant and environment are
fine, an incident id is not.

## Adding a span

Use the right source and a name from `SreSpanNames`. Guard expensive tag
computation with `ActivitySource.HasListeners()`; `SetSafeTag` already handles the
cheap case.

## The sampler

`InvestigationAwareSampler` is the **top-level** sampler. It returns
`RecordAndSample` for `incident.investigation` by name and delegates everything
else to `ParentBased(TraceIdRatioBased(ratio))`.

Do not wrap it in a `ParentBasedSampler`. That wrapper only consults its inner
sampler for *root* spans, and an investigation never is one — it starts inside the
Kafka consumer span, which continues the trace of the HTTP request that accepted
the alert. Wrapped, the ingress ratio decided everything and at 0.1 nine
investigations in ten vanished (D27). `Infrastructure.Tests/SamplingTests` locks
this; do not weaken it.

## Where it goes

Workers export over OTLP only. `GET /metrics` exists on the API alone, because a
second HTTP listener whose only job is metrics complicates the one thing a worker
does, and the collector is already the aggregation point (D22).

## Verify

```bash
bash eng/ci.sh
```

`SecurityTests/SpanAttributeLeakTests` asserts the allowlist contains nothing
resembling `prompt.text`, a token or a credential, and that a non-allowlisted key
is dropped and counted. If you added a key, that test is the one to read first.

Then look at it:

```bash
docker compose -f deploy/docker/compose.yaml up -d --build
# send an alert, then open Jaeger at http://localhost:16686
```

The compose stack sets `Telemetry__HttpSamplingRatio: "1.0"` so the whole chain —
api, alert-worker, investigation-worker — lands in one trace. Confirm the new
attribute appears and that no span carries anything 16.1 forbids.

## Related

- `docs/DECISIONS.md` D13, D14, D22, D27
- `docs/RUNBOOK.md`, "Investigations are missing from Jaeger" and
  "`sre_trace_attribute_rejected_total` is climbing"
