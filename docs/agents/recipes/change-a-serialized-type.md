# Change a serialized type

Four separate outages in this repository's short history came from this one task,
and every one of them compiled. Read this before adding a property to anything
that crosses a boundary.

Reflection-based serialization is banned (D10) and enforced by the banned-API
list, so every serialized type needs compile-time metadata in a generated context.
The generator only knows what it was told.

## Which context

`docs/agents/MAP.md` has the table. A type can need more than one — an activity
result that is also persisted as `jsonb` and returned over HTTP belongs in three.

| The type crosses | Context |
|---|---|
| HTTP or Kafka | `Contracts/Serialization/SreAiAgentJsonContext.cs` |
| the model boundary | `Infrastructure/Llm/LlmJsonContext.cs` |
| a Temporal activity | `Workflow/Temporal/WorkflowPayloadJson.cs` |
| a `jsonb` column | `Infrastructure/Persistence/JsonbConverters.cs` |
| into a prompt as a tool result | `Infrastructure/Tools/Adapters/ToolResultEnvelopes.cs` |

## The four ways this goes wrong

### 1. The generator stops at a custom converter

A `JsonConverter` is a black box to the source generator. It will not walk into
the type the converter handles, so **none of the types reachable only through that
converter get metadata**.

`Result<T>` has a converter factory, so its eight success types had to be listed
explicitly in `WorkflowPayloadJson`. Until they were, Temporal failed with
`NoMetadataForType` — *after* the workflow had started, having already spent the
model calls.

If you add a converter, or add a type that is only ever reached through one, list
every type behind it by hand.

### 2. A `Task`-returning workflow method

`[WorkflowRun] public async Task RunAsync(...)` makes the workflow result the
internal BCL type `VoidTaskResult`, which no generated context can name. Every
activity succeeds, the incident resolves, and then the final workflow task fails
on the way out and retries forever — holding the workflow id, so a retry of that
incident is refused as already running.

Return a real type. `TemporalInvestigationWorkflow.RunAsync` returns
`InvestigationStatus` for exactly this reason (D28).

### 3. `required` members stop property initializers running

`System.Text.Json` does not run property initializers on a type that has any
`required` member. A defaulted optional member arrives as `null` or `0` no matter
what the initializer says.

Make every optional member on such a type nullable and handle the absence
explicitly. `CreateIncidentRequest` did not, so optional fields arrived empty and
investigations never started. `Contracts.Tests/AbsentMemberTests` locks this.

### 4. Casing

Wire contracts are **snake_case** — `JsonNamingPolicy.SnakeCaseLower` on the API
and `JsonKnownNamingPolicy.SnakeCaseLower` on the contexts. Temporal payloads are
snake_case with string enums too, because workflow history is read by people
during an incident and an integer enum is a lookup nobody wants at 3am.

## Procedure

1. Change the type.
2. Add `[JsonSerializable(typeof(T))]` to every context from the table above.
   For Temporal, listing the root type is enough — the generator walks into what
   it references — **unless** a custom converter sits in the way.
3. If it is stored in `jsonb`, check the converter in `JsonbConverters.cs`.
   PostgreSQL reparses and reformats `jsonb`, so key order and whitespace are not
   preserved. Never hash or sign a payload and expect the stored copy to match.
4. Extend the round-trip test. Do not write a fixture — serialize the real type.

## Verify

```bash
bash eng/ci.sh
```

The suites that own this:

| Suite | Asserts |
|---|---|
| `Contracts.Tests` | Wire names, casing, absent members |
| `Workflow.Tests/WorkflowPayloadTests` | Every activity argument and return type converts |
| `Infrastructure.Tests/JsonbRoundTripTests` | Stored types survive the database round trip |

`WorkflowPayloadTests` exists because a schema written from the wire contract
rather than from the type was wrong once already — the first `AgentTurn` schema
said `tool_name` where the type says `tool`. Serializing the real type is what
caught it.

If the change touches Temporal, the gate is not enough. Run the stack with
`Workflow__Engine: temporal` and watch a workflow reach `Completed`:
`docs/agents/recipes/verify-end-to-end.md`.

## Related

- `docs/DECISIONS.md` D10, D28
- `docs/RUNBOOK.md`, "A workflow stays Running after the incident closed"
