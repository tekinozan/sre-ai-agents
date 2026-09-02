# Resume the tenant model-provider work

## Durum

- [x] S0 — Give plans a permanent home
- [x] S1 — Make the tree compile
- [x] S2 — Composition-root cutover
- [x] S3 — Bootstrap the tenant assignment
- [x] S4 — One migration for the whole control plane
- [x] S5 — Close correctness gaps
- [x] S6 — Migrate and extend tests
- [x] S7 — Put the eval harness on the production pipeline
- [x] S8 — Tenant management API
- [x] S9 — Telemetry and decision record
- [x] Verification — gates and end-to-end scenarios

## Context

A large change set implementing `docs/plans/llm-providers.md` (tenant-owned LLM
provider connections, immutable revisions, model deployment versions, model
assignment releases, a per-candidate provider resolver replacing the single global
`IChatClient`, and a new `tests/SreAiAgent.Evals` harness) had been drafted in an
earlier session and left uncommitted when that session ended mid-cutover. At that point,
none of the changes had been committed to `main`.

At the resume point, the tree did not build, the runtime service graph could not
be constructed, the database schema had no migration for any of the ten new
tables, and the management API did not exist. This plan finished the increment
through phase 3 of the original document (four adapters + tenant management API +
exact pinning), operational Phase 5 foundations (production guard, CI smoke, telemetry,
audit, secret-leak checks, full-stack exact pinning), made the eval harness run the
real pipeline on the deterministic scripted slice, and established a durable home
for plan documents so a future interrupted session would not require reconstruction.

Language: this plan and every new plan document are English, matching `AGENTS.md`,
`ARCHITECTURE.md` and `docs/DECISIONS.md`. `docs/plans/llm-providers.md` remains
Turkish; its status and remaining roadmap are maintained.

## Current state — verified this session (2026-09-02)

All implementation steps (S0–S9) and end-to-end verification gates are complete:

- **Build & formatting:** `dotnet format --verify-no-changes` and `dotnet build SreAiAgent.slnx` passed cleanly with 0 warnings and 0 errors.
- **Test suite & CI:** `sh eng/ci.sh --with-integration` passed 348 unit/security/eval tests, deterministic scripted eval smoke, 16 PostgreSQL integration tests, 11 contract tests, and the vulnerable package audit; matching PowerShell CI wiring (`eng/ci.ps1`) is in place.
- **Alert workflow & exact pinning:** Compose alert flow reached `Resolved`/`Complete`. Exact assignment pinning was confirmed in the configuration snapshot and across four `llm_calls` with the scripted provider and non-empty deployment configuration hashes.
- **Management API scenario:** Raw-secret rejection, environment-reference credential storage, evaluation, activation, and trigger flow passed.
- **Live eval failure mode:** Explicit live evaluation requests failed loudly with clear errors when credentials or candidate configurations were absent, with zero silent fallback to scripted mocks.
- **Security & leaks:** Leak audit clean across eval artifacts, span attribute constants, and audit-writing/configuration paths.

## Approach
Steps are ordered so the tree builds and the existing suite passes at each numbered
boundary. S1–S4 are one commit-worthy unit (nothing works before all four).
S5 onward are independent of each other except where stated.

### S0 — Give plans a permanent home (do first)

The reason this is first: the previous session's loss is what created this work.

1. Create `docs/plans/README.md`:
   - one paragraph: `docs/plans/` holds execution plans for multi-session work;
     a plan stays in the repository from the moment it is written until every step
     is done or explicitly dropped; decisions that outlive the plan graduate to
     `docs/DECISIONS.md`; the plan is then marked `Durum: tamamlandı` and kept,
     not deleted, because the reasoning behind a half-finished increment is what a
     resumed session needs.
   - a table of plan files: path, one-line subject, status.
2. In `docs/plans/llm-providers.md`, replace lines 1–10 (the HTML comment telling
   the reader to delete the file) with:
   ```markdown
   > **Durum:** faz 1-3 kısmen uygulandı, devam ediyor.
   > **Devam planı:** `docs/plans/llm-providers-resume.md`.
   > **Son güncelleme:** <today, YYYY-MM-DD>.
   > Kalıcı kararlar tamamlandıkça `docs/DECISIONS.md`'ye D33+ olarak taşınır;
   > bu dosya silinmez.
   ```
3. Copy this plan verbatim to `docs/plans/llm-providers-resume.md` and add a
   `## Durum` block at the top listing S0–S9 with `[ ]` boxes. Tick a box in the
   same commit that lands the step. This file is the resume point if the session
   dies again.
4. Append to `AGENTS.md` under `## Conventions`, after the **Adding a decision**
   paragraph:
   ```markdown
   **Plans live in the repository.** Multi-session work gets a file in
   `docs/plans/` with a status block, committed before the first code change and
   updated as steps land. A plan that only exists in a chat session is lost with
   the session; this has already cost one reconstruction.
   ```

### S1 — Make the tree compile

Four edits, no behaviour change beyond the retry loop's shape.

1. `src/SreAiAgent.Infrastructure/Llm/LlmGateway.cs` — delete the
   `Result<LlmResult<T>>? lastFailure` local (line 96) and the trailing
   `return lastFailure ?? Fail(...)` (lines 154-160). Inside the loop body use a
   non-nullable local:
   ```csharp
   Result<LlmResult<T>> outcome = response.IsFailure
       ? Fail(request, activity, Elapsed(startTimestamp), response.Error, Accounting.Empty, "failed")
       : Interpret(request, response.Value, Elapsed(startTimestamp), activity);

   if (outcome.IsSuccess
       || attempt == _options.MaxRetries
       || !ErrorCatalog.IsRetryable(outcome.Error.Code))
   {
       return outcome;
   }
   ```
   After the loop, return the same `Fail(...)` expression that stood at 154-160,
   with a comment stating it is unreachable because the loop returns on
   `attempt == _options.MaxRetries` and the compiler cannot prove it.
2. `src/SreAiAgent.Infrastructure/Llm/Providers/ScriptedProviderChatClient.cs` —
   change the primary constructor from `(TimeProvider timeProvider)` to
   `(ScriptedChatClient client)` and drop the `_client = new(timeProvider)` field
   initializer, holding the injected instance instead. CA1001 fires only on a
   disposable field the type creates; an injected one is owned by the container.
3. `src/SreAiAgent.Infrastructure/Llm/Providers/ProviderClientResolver.cs` —
   replace `ProviderClientCatalog`'s `TimeProvider timeProvider` parameter with
   `ScriptedChatClient scripted` (it was used only by the scripted branch at line
   21) and pass `scripted` there.
4. `src/SreAiAgent.Infrastructure/Llm/Providers/OpenAiProviderChatClient.cs:110` —
   replace `payload?.Choices?.FirstOrDefault()` with an index guard:
   ```csharp
   OpenAiChoice? choice = payload?.Choices is { Count: > 0 } choices ? choices[0] : null;
   ```

`dotnet build src/SreAiAgent.Infrastructure` must be clean before continuing; the
test-project errors are S6.

### S2 — Composition-root cutover

`LlmGateway` already requires `IChatClientResolver`; nothing registers it. Complete
the cutover in one pass and delete the legacy path.

1. Add `src/SreAiAgent.Infrastructure/Llm/Providers/IProviderRevisionReader.cs`:
   ```csharp
   internal interface IProviderRevisionReader
   {
       Task<ProviderConnectionRevision?> GetRevisionAsync(
           string tenant,
           ProviderConnectionRevisionId revisionId,
           CancellationToken cancellationToken);
   }
   ```
   Implement it on `ModelProviderRepository` (the method body already exists there
   for the resolver's current direct call — reuse it, do not add a second query),
   and change `ChatClientResolver`'s first constructor parameter from
   `ModelProviderRepository providers` to `IProviderRevisionReader providers`.
   Reason: the eval harness must resolve a provider client without a database, and
   this is the only repository member the resolver uses.
2. Add `src/SreAiAgent.Infrastructure/Llm/Providers/ProviderHttpClients.cs`
   registration alongside the existing `Chat` constant, modelled on
   `Observability/Providers/Shared/ObservabilityHttpClients.cs`:
   ```csharp
   services.AddHttpClient(ProviderHttpClients.Chat)
       .ConfigureHttpClient(static client =>
       {
           // The pinned deployment timeout is the only deadline, and LlmGateway
           // enforces it with a linked token. A second, lower HttpClient deadline
           // would surface as a cancellation no adapter catch maps.
           client.Timeout = Timeout.InfiniteTimeSpan;
       });
   ```
   Deliberately **no** `AddStandardResilienceHandler`: `LlmGateway` already owns the
   retry budget, the error taxonomy and the audit row, and a second retry layer
   would double-charge a paid call without recording it.
3. Rewrite `AddSreLlm` (`DependencyInjection.cs:105-128`). Delete
   `services.TryAddSingleton<IChatClient, ScriptedChatClient>();`, delete the now
   unused `using Microsoft.Extensions.AI;` (IDE0005 is an error), and replace the
   `<remarks>` block, whose text still claims the `IChatClient` line is the only
   provider switch. New registrations:
   ```csharp
   services.TryAddSingleton<IRedactor, RegexRedactor>();
   services.TryAddSingleton<IPromptRenderer, ControlledPromptRenderer>();
   services.TryAddSingleton<StructuredOutputValidator>();
   services.TryAddSingleton<ScriptedChatClient>();
   services.TryAddSingleton<ProviderEndpointPolicy>();
   services.TryAddSingleton<ICredentialResolver, ExternalReferenceCredentialResolver>();
   services.TryAddSingleton<ProviderClientCatalog>();
   services.TryAddSingleton<ProviderRevisionVerifier>();
   services.AddProviderHttpClient();
   // Scoped: it caches a provider client per immutable revision id and follows
   // the scoped revision reader, so a revision replaced mid-process is picked up
   // by the next activity rather than surviving in a process-wide cache.
   services.TryAddScoped<IChatClientResolver, ChatClientResolver>();
   services.TryAddScoped<IModelProviderControlPlane, ModelProviderControlPlane>();
   services.TryAddScoped<ILlmGateway, LlmGateway>();
   ```
4. In `PersistenceDependencyInjection.AddSrePersistence`, after line 78, add:
   ```csharp
   services.TryAddScoped<ModelProviderRepository>();
   services.TryAddScoped<IProviderRevisionReader>(
       provider => provider.GetRequiredService<ModelProviderRepository>());
   services.TryAddScoped<IConfigurationPinner, ConfigurationPinner>();
   ```
   and **remove** `services.TryAddScoped<IConfigurationPinner, ConfigurationPinner>();`
   from `AddSreRegistries` (`DependencyInjection.cs:92`). The pinner now reads the
   database, so it belongs with the persistence registrations; leaving it in
   `AddSreRegistries` is what makes the eval harness's registry-only container
   unbuildable.
5. Make capability truth single-sourced: change every adapter's
   `CapabilityDescriptor` property to `ProviderClientCatalog.Descriptor(<kind>)`,
   and change the catalog's `_ =>` arm from `new(adapterKind, ModelCapability.None)`
   to `throw new InvalidOperationException("The provider adapter kind is not registered.")`,
   matching `Create`. Two switches that must agree is one switch too many.

### S3 — Bootstrap the tenant assignment

Without an active `ModelAssignmentRelease`, `PinCurrentAsync` fails and every
incident fails. There is no management API yet and there will be none in
Development, so the shipped configuration must be able to seed one.

1. Extend `LlmOptions` (`Configuration/SreOptions.cs`) with a nested bootstrap
   section. Exact keys and defaults:
   ```csharp
   public BootstrapOptions? Bootstrap { get; init; }

   public sealed class BootstrapOptions
   {
       [Required(AllowEmptyStrings = false)] public string Tenant { get; init; } = string.Empty;
       [Required(AllowEmptyStrings = false)] public string ConnectionDisplayName { get; init; } = "default";
       [Required] public ProviderAdapterKind AdapterKind { get; init; } = ProviderAdapterKind.Scripted;
       [Required(AllowEmptyStrings = false)] public string Endpoint { get; init; } = string.Empty;
       public string? Region { get; init; }
       public string? ApiVersion { get; init; }
       public string? Organization { get; init; }
       [Required(AllowEmptyStrings = false)] public string CredentialRef { get; init; } = string.Empty;
       [Required(AllowEmptyStrings = false)] public string AssignmentName { get; init; } = "bootstrap";
       [Required(AllowEmptyStrings = false)] public string ClassifierProfile { get; init; } = "fast";
       [Required(AllowEmptyStrings = false)] public string CorrelatorProfile { get; init; } = "analytical";
   }
   ```
   Configuration keys are `Llm:Bootstrap:Tenant`, `Llm:Bootstrap:AdapterKind`, and
   so on. Absent section = no bootstrap.
2. Add `src/SreAiAgent.Infrastructure/Llm/ModelAssignmentBootstrapper.cs`, an
   `IHostedService` registered by `AddSrePersistence` **after**
   `AddHostedService<DatabaseMigrator>()` so the schema exists. Behaviour:
   - No `Llm:Bootstrap` section → return immediately.
   - `ModelProviderRepository.GetActiveSnapshotAsync(tenant, ct)` returns non-null
     → return immediately. This is the idempotency check; re-running is a no-op.
   - Otherwise, in one `IUnitOfWork.ExecuteInTransactionAsync`:
     - `ProviderConnection.Create` + `ProviderConnectionRevision.Create` with
       revision `1`, the configured adapter/endpoint/region/api version/
       organization/credential ref, and declared capabilities taken from
       `ProviderClientCatalog.Descriptor(adapterKind).Capabilities`.
     - One `ModelDeploymentVersion.Create` per distinct model profile referenced by
       the shipped configuration: the classifier profile, the correlator profile,
       and every released agent's `AgentDefinition.ModelProfileRef`. Resolve each
       through `IModelProfileRegistry.ResolveAsync(RegistryOptions.ModelRelease, ref, ct)`
       and map `ModelProfile` → deployment: logical name = profile name, version 1,
       `ProviderModel` = the profile's resolved concrete model name, temperature,
       `MaxOutputTokens`, timeout, `InputCostPerMillionTokens`,
       `OutputCostPerMillionTokens`, currency `"USD"`, provider parameters empty,
       required capabilities derived from the profile's `structured_output`,
       `tool_calling`, `parallel_tool_calls` booleans plus `SystemMessage`.
     - One `ModelAssignmentRelease.Create` version 1 binding classifier and
       correlator to their profiles' deployments and each released agent name to
       its profile's deployment.
     - A `ProviderRevisionVerificationEntity` row. In Production, obtain it by
       calling `ProviderRevisionVerifier.VerifyAsync` and **fail startup** on
       failure (a misconfigured process must die at deploy time, per `AGENTS.md`).
       Outside Production, write the catalog descriptor's capabilities without a
       network call.
     - The `TenantActiveModelAssignmentEntity` pointer.
   - Log one `[LoggerMessage]` line naming tenant, assignment name/version and
     configuration hash. Never log the credential reference's resolved value.
3. Configure the bootstrap for local and test runs. In
   `src/SreAiAgent.Api/appsettings.Development.json`,
   `src/SreAiAgent.AlertWorker/appsettings.Development.json`,
   `src/SreAiAgent.InvestigationWorker/appsettings.Development.json` and the
   compose env files under `deploy/docker/`, set:
   `Llm:Bootstrap:Tenant` = the same tenant the development authentication handler
   issues, `AdapterKind` = `Scripted`, `Endpoint` = `https://scripted.invalid/`,
   `CredentialRef` = `env://SRE_SCRIPTED_CREDENTIAL`. `ProviderEndpointPolicy`
   already refuses `Scripted` in Production, which is the phase-5 production
   scripted guard — S6 adds the test that pins it.
   Production configuration files get no bootstrap section.

`ModelProfileRef` and `config/models/*.yaml` survive this step as the **seed**
for the bootstrap, not as a runtime resolution path: nothing outside the
bootstrapper calls `IModelProfileRegistry` at request time after S2, and
`RegistryValidator` keeps validating the seed at startup.

### S4 — One migration for the whole control plane

`dotnet ef migrations add ModelControlPlane --project src/SreAiAgent.Infrastructure --startup-project src/SreAiAgent.Api`
(`DesignTimeDbContextFactory.cs` exists). Never hand-edit the snapshot. The
generated migration must cover, and be reviewed for:

- The ten new tables listed in `Persistence/Configurations/ModelProviderConfigurations.cs`.
- `configuration_snapshots`: drop `model_release`, add `model_assignment_json jsonb`.
- `llm_calls`: the new tenant / assignment / deployment / provider-revision id and
  hash columns, requested vs observed provider and model, provider request id and
  finish reason; drop the replaced `model_provider` / `model_name`.
- `agent_runs`: rename `model_profile_version` → `model_deployment_version`,
  together with renaming `AgentExecutionIdentity.ModelProfileVersion` and
  `AgentRun.ModelProfileVersion` to `ModelDeploymentVersion` and the mapping at
  `Configurations/InvestigationConfigurations.cs:61`. The property holds
  `ModelCandidate.DeploymentVersion` since the cutover, so the current name lies.

Then hand-append to the generated `Up` (the repository has no raw SQL in a
migration yet; this is the first, and it is justified because an application-level
guard does not bind a psql session):

```csharp
migrationBuilder.Sql("""
    CREATE FUNCTION sre_reject_row_mutation() RETURNS trigger AS $$
    BEGIN
        RAISE EXCEPTION 'MODEL_CONFIGURATION_INVALID: % rows are immutable', TG_TABLE_NAME;
    END;
    $$ LANGUAGE plpgsql;
    """);
```

and one `BEFORE UPDATE OR DELETE ... FOR EACH ROW EXECUTE FUNCTION sre_reject_row_mutation()`
trigger on `provider_connection_revisions`, `model_deployment_versions`,
`model_assignment_releases` and `provider_revision_verifications`; on
`model_assignment_agent_slots` the trigger is `BEFORE UPDATE` only. Change that
table's FK to `model_assignment_releases` from `Cascade` to `Restrict` in
`ModelProviderConfigurations.cs` before generating the migration — a cascade that
deletes rows of an immutable release contradicts the guard. `Down` drops the
triggers and the function.

Also in `ModelProviderConfigurations.cs` before generating: give
`ProviderConnectionEntity.ActiveRevisionId` a composite `(tenant, active_revision_id)`
FK to `provider_connection_revisions(tenant, id)` with `Restrict`. It currently has
no FK at all, so a writer can point a connection at another tenant's revision.

### S5 — Close the correctness gaps the design forbids

Each item is independent; all are small and all defend a rule the original document
states explicitly.

1. **Raw secrets must not reach the database.** `ModelProviderControlPlane.CreateRevisionAsync`
   persists `command.CredentialRef` unchecked, so `sk-...` pasted into the field is
   stored. Validate the scheme before constructing the revision: accept only values
   starting `env://` or `file://` (the two forms `ExternalReferenceCredentialResolver`
   supports); otherwise return
   `Error.Permanent(DomainErrorCodes.ModelConfigurationInvalid, "A credential reference must use env:// or file://.")`.
   Expose the accepted prefixes as `internal const` on `ExternalReferenceCredentialResolver`
   and use them from both sides so the two never drift.
2. **A new revision must not become active before it resolves.** `CreateRevisionAsync`
   sets `ProviderConnectionEntity.ActiveRevisionId` in the same save. Keep that, but
   first call `ICredentialResolver.ResolveAsync(command.CredentialRef, ct)` and
   return its failure — a reference that does not resolve is a configuration error,
   not a deferred one. Do not log or persist the resolved value.
3. **Usage must not vanish on a null-content success envelope.**
   `OpenAiCompatibleProviderChatClient.SendAsync` turns a 200 with usage but no
   choice content into a bare `Result.Failure`, discarding tokens the provider will
   bill. Return a successful `ProviderChatResponse` with `Text = string.Empty` and
   the reported usage instead; `LlmGateway.Interpret` already maps empty text to
   `LLM_STRUCTURED_OUTPUT_INVALID` with the accounting attached (outcome `"empty"`).
4. **Seed must not be silently dropped.** Three holes: `AnthropicProviderChatClient`
   has no seed field, `ScriptedProviderChatClient` never forwards it,
   `OpenAiCompatibleProviderChatClient` sends it though its descriptor omits `Seed`.
   Resolve at the boundary rather than per adapter: in
   `ModelProviderControlPlane.CreateDeploymentAsync`, when `ProviderParameters`
   contains `seed`, require both that the value parses as `int` and that
   `RequiredCapabilities` includes `ModelCapability.Seed`; otherwise return
   `MODEL_CONFIGURATION_INVALID`. Activation already rejects a deployment whose
   required capabilities exceed the verified set, so an Anthropic or
   OpenAI-compatible deployment can no longer carry a seed. Remove the seed field
   from the OpenAI-compatible request path only if it is shared with OpenAI — it is,
   so leave it; the capability gate is now what prevents the mismatch. In
   `LlmGateway`, an unparseable `seed` is now unreachable; replace the silent
   `TryParse` fallback with `int.Parse(..., CultureInfo.InvariantCulture)` guarded by
   `TryGetValue`, so a future regression throws rather than degrades.
5. **Expected failures must not throw.** `RequestEvaluationAsync` calls
   `Required(dataset/baseline)` and `ActivateAsync` calls `endpointPolicy.Validate`
   outside any catch, so both `Result<T>` methods can throw `DomainException` /
   `InvalidOperationException`. Wrap both in the same `try/catch (DomainException)`
   / `catch (InvalidOperationException)` shape the other methods already use, and
   add `catch (DbUpdateException exception) when (exception.InnerException is
   PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })` around every
   `SaveChangesAsync` in the control plane, returning a new error code (item 6).
   Copy the predicate shape from `Messaging/KafkaConsumerService.IsDuplicateDelivery`.
6. **New error code.** `ApplicationErrorCodes.ModelResourceConflict = "MODEL_RESOURCE_CONFLICT"`,
   registered in `ErrorCatalog` as HTTP 409, not retryable, description
   `"A model control-plane resource with that identity already exists."` It is
   returned by item 5's unique-violation catch, which is also what makes the
   `MAX(version) + 1` race safe: the loser of the race gets a stable 409 rather than
   a `DbUpdateException`.
7. **Activation must check declared capabilities too.** `ActivateAsync` compares the
   candidate against the latest verification only. Add the revision's
   `DeclaredCapabilities` to the same predicate, so a deployment requiring more than
   its revision declares fails with `MODEL_CAPABILITY_MISMATCH` before any network
   call.
8. **Close the evaluation gate.** Nothing sets an evaluation to passed, so
   `Llm:RequireEvaluationForActivation = true` makes activation impossible. Add to
   `IModelProviderControlPlane`:
   ```csharp
   Task<Result<ModelAssignmentEvaluationHandoff>> CompleteEvaluationAsync(
       string tenant,
       ModelAssignmentEvaluationId evaluationId,
       bool passed,
       string artifactHash,
       string actor,
       CancellationToken cancellationToken);
   ```
   The implementation loads the tenant's evaluation row, rejects a row already in a
   terminal status with `MODEL_RESOURCE_CONFLICT`, sets `Status` to `"passed"` or
   `"failed"`, `Passed`, `ArtifactHash`, `CompletedAt = timeProvider.GetUtcNow()`,
   writes a control-plane audit row in the same save. The eval CI job posts the
   result through the endpoint added in S8.
9. **Snapshot resolution must not throw on a broken row.**
   `ModelProviderRepository.BuildSnapshotAsync` indexes `deployments[id]` and
   `revisions[...]` directly, so a missing row is a `KeyNotFoundException` inside a
   `Result`-returning path. Use `TryGetValue` and return `null`, which
   `ConfigurationPinner` already maps to `MODEL_ASSIGNMENT_NOT_FOUND`.
10. **Fix the audit contract's documentation.** `Audit/IAuditSink.cs`'s XML doc still
    names the removed `ModelProvider` / `ModelName` parameters. Rewrite the
    `<param>` list to the record's current members.

### S6 — Test call sites and the tests the new behaviour needs

1. Add a shared fixture. `tests/SreAiAgent.TestKit` is already visible to the test
   projects (`InternalsVisibleTo`); add `PinnedModelFixtures.cs` there exposing
   `static PinnedModelAssignment Scripted(string tenant = "acme")` that builds a
   `PinnedModelAssignment` with `Classifier`, `Correlator` and one
   `"database-expert"` agent slot, all `ProviderAdapterKind.Scripted`, deterministic
   ids from fixed `Guid`s, non-zero costs, and hashes computed the same way
   `ModelAssignmentRelease.Create` computes them. Every migrated call site uses it.
2. `tests/SreAiAgent.Application.Tests/UseCases/CreateIncidentHandlerTests.cs:38-46`
   — `_pinner.PinCurrentAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())`
   returning `Result<ConfigurationSnapshot>.Success(...)` with
   `PinnedModelFixtures.Scripted()` in position 4. Add one test asserting the handler
   propagates a pin failure instead of opening an incident, and one asserting the
   tenant reaches the pinner.
3. `tests/SreAiAgent.Workflow.Tests/RecordingActivities.cs:75-86` and
   `WorkflowPayloadTests.cs:144-151` — same substitution. In
   `EveryActivityReturnTypeHasSerializationMetadata`, assert the round-tripped
   `ResolvedConfiguration.ModelAssignment.Classifier.DeploymentConfigurationHash`
   survives, which is what proves the Temporal converter reaches inside the new
   graph.
4. `src/SreAiAgent.Workflow/Temporal/WorkflowPayloadJson.cs` — `PinnedModelAssignment`
   and `PinnedModelCandidate` are reached through `ConfigurationSnapshot`, which is
   already a registered root, so no new `[JsonSerializable]` is needed; confirm with
   the test in item 3 rather than by inspection.
5. New `tests/SreAiAgent.Infrastructure.Tests/ProviderAdapterTests.cs` — one
   `[Theory]` over the four HTTP adapters against a stub `HttpMessageHandler`
   registered on the `sre-llm-provider` named client, asserting for each: a
   structured 200 maps text/usage/observed model/request id/finish reason; 401 →
   `LLM_AUTHENTICATION_FAILED`; 429 → `LLM_RATE_LIMITED`; 503 → `LLM_UNAVAILABLE`;
   400 → `LLM_INVALID_REQUEST`; a 200 with usage and null content yields empty text
   with usage preserved (S5.3); the outbound request never carries the credential in
   a logged or serialized form.
6. New `tests/SreAiAgent.Infrastructure.Tests/ChatClientResolverTests.cs` — a
   candidate whose `ProviderConfigurationHash`, tenant, revision number or adapter
   kind disagrees with the revision the reader returns is refused; two candidates on
   the same revision id reuse one client; an unknown adapter kind is
   `CONFIG_INVALID`; there is no path that returns a scripted client for a
   non-scripted candidate.
7. New `tests/SreAiAgent.SecurityTests/ProviderEndpointPolicyTests.cs` — in
   Production, `ProviderAdapterKind.Scripted` is refused for every endpoint, an IP
   literal host is refused, a host outside `Llm:AllowedCustomEndpointHosts` is
   refused for `OpenAiCompatible`, and plain HTTP is refused. This is the phase-5
   production scripted guard.
8. New `tests/SreAiAgent.IntegrationTests/ModelControlPlaneTests.cs` in the existing
   `PostgresCollection`, `[Trait("Category", "Integration")]` — the migration applies
   cleanly; tenant A cannot read or reference tenant B's connection, revision,
   deployment version or assignment release; an `UPDATE` on
   `provider_connection_revisions` raises the trigger; creating a second deployment
   version with the same `(tenant, logical_name, version)` returns
   `MODEL_RESOURCE_CONFLICT`; the bootstrapper is a no-op on a second run.
9. `tests/SreAiAgent.Infrastructure.Tests/ShippedConfigurationTests.cs:225,250` keeps
   using `ScriptedChatClient` directly — that is still correct after S2, because the
   scripted adapter wraps exactly that type. Update only the message at line 248,
   which claims "V1 ships one IChatClient", to say the scripted adapter is the only
   adapter the shipped bootstrap selects.

### S7 — Put the eval harness on the production pipeline

The harness currently duplicates correlation, preselects the agent from the dataset
and copies `ExpectedRoutingFamilies[0]` into its own output, which makes the routing
gate tautological. Replace the duplication with the real activities.

1. Add `tests/SreAiAgent.Evals/Harness/InMemoryInvestigationStores.cs` implementing
   `IIncidentRepository` (4 members), `IInvestigationStore` (8),
   `IConfigurationSnapshotStore` (2) and `IUnitOfWork` (2) over dictionaries.
   `ExecuteInTransactionAsync` just invokes the operation. Nothing touches Postgres,
   Kafka or Temporal.
2. Rewrite `ProductionEvalRuntime` to construct the production
   `InvestigationActivities` with those stores plus the production
   `IIncidentRouter`, `IAgentExecutor`, registries, `IPromptRenderer`, `ILlmGateway`,
   `SreMetrics`, the deterministic `TimeProvider` and `NullLogger`. Per case, drive
   the real sequence: `LoadIncidentAsync` → `ResolveConfigurationAsync` →
   `ClassifyIncidentAsync` → `SelectAgentsAsync` → `RunAgentAsync` →
   `CorrelateFindingsAsync`. Delete `CorrelateAsync`, the hard-coded
   `evalCase.ExpectedRoutingFamilies[0]`, the unused `using Microsoft.Extensions.AI;`
   and the unused `long before = _audit.LlmCalls.Count;`.
   `EvalOutput.RoutingFamily` now comes from `IncidentClassification.IncidentTypes`,
   and the dataset's `agent` field becomes an assertion — the case fails if
   `SelectAgentsAsync` does not select it — rather than an input.
3. Register the resolver path in the eval container: an `IProviderRevisionReader`
   returning the single scripted `ProviderConnectionRevision` the candidate pins, an
   `IHostEnvironment` stub reporting `Development` (`ProviderEndpointPolicy` needs
   it), `IOptions<LlmOptions>`, and `AddSreLlm()`. `AddSreRegistries()` no longer
   drags in `IConfigurationPinner` after S2.4, so `ValidateOnBuild = true` succeeds
   without a database.
4. Make `PinnedCandidateFactory` live and honest. It builds the scripted
   `ProviderConnectionRevision` with `ProviderConnectionRevision.Create` and derives
   every hash from it instead of hard-coding, and it takes per-slot temperature,
   max output tokens, timeout and the two cost-per-million values from the resolved
   `ModelProfile` for that slot rather than zero. Zero prices make the cost gate
   vacuous.
5. Fix the accounting the interrupted work left: aggregate usage across the whole
   case including the failure path (the audit sink already records every call — sum
   `EvalAuditSink.LlmCalls` for the case instead of only the correlator result), and
   report end-to-end case latency rather than the last LLM call's latency.
6. Make `EvalRunner` honour the frozen policies it currently only hashes:
   `warmup_runs` executes and its attempts are excluded from the aggregate,
   `concurrency` bounds a `Parallel.ForEachAsync` over cases (repeats stay
   sequential within a case for stable ordering), `timeout_seconds` becomes the
   per-case deadline instead of the hard-coded 60/30 seconds. Add the run's start
   timestamp to the run id so two runs of the same candidate do not collide on the
   immutable artifact directory.
7. Delete `tests/SreAiAgent.Evals/Harness/QualityJudge.cs`, the `judge` section of
   `eval-config.v1.json`, the judge fields of `CandidateFreezer`'s frozen identity
   and `JudgeArtifact` from the artifact models. It is never constructed, and what
   it would write is the configured minimum agreement presented as a measurement.
   The calibrated judge is phase 4 and will be built against a versioned judge
   schema; a prototype that reports its own threshold as its result is worse than
   its absence.
8. Widen the scripted smoke: tag `app-latency-v1`, `kubernetes-deployment-v1`,
   `prompt-injection-v1`, `policy-refusal-v1`, `unsupported-claims-v1`,
   `timeout-truncation-v1` and `missing-conflicting-evidence-v1` with
   `scripted-smoke` alongside the existing `database-pool-timeout-v1`, so the
   always-on CI gate covers refusal, injection, truncation and routing rather than
   one happy path. `ScriptedChatClient` must recognise each case's agent and tools —
   `ShippedConfigurationTests` already enforces that for released agents.
9. Tighten three scorer semantics that currently pass by construction:
   - citations must be a subset of the evidence ids the tools **actually returned**
     during the run (`FixtureToolGateway` records them), not of all declared
     fixtures;
   - the per-case `rubric.minimum_quality` failure must be part of `HardScore.Passed`
     and therefore of `EvalAggregator.HardFailureCount`, not only a string in
     `EvalScore.Failures`;
   - `FixtureToolGateway.LooksLikeWrite`'s name heuristic is replaced by the tool
     registry's `RiskLevel`, which is the authority the production gateway uses.
10. Regenerate the baseline **last**, after 1–9 are green: run the scripted smoke
    with `SRE_EVAL_WRITE_BASELINE=1` (add that switch to `EvalRunner`), and commit
    the produced `Baselines/scripted-smoke-<date>.json` with real dataset, scorer,
    candidate hashes and observed aggregate. Delete the placeholder file. Never
    special-case the `*_PENDING` sentinels.

### S8 — Tenant management API

Copy the endpoint pattern of `src/SreAiAgent.Api/Endpoints/IncidentEndpoints.cs`:
a typed route group, `RequireAuthorization`, `WithSummary`, tenant from
`principal.Tenant()`, actor from `principal.Actor()`, and `result.ToHttpResult(...)`.

1. `src/SreAiAgent.Api/Security/SreRoles.cs` — add
   `public const string ManageModelProviders = "model-providers:manage";` to
   `SrePolicies`. In `SecurityDependencyInjection.AddSreSecurity`, add:
   ```csharp
   .AddPolicy(SrePolicies.ManageModelProviders, policy => policy
       .RequireAuthenticatedUser()
       .RequireClaim(SreClaims.Tenant)
       .RequireRole(SreRoles.Operator, SreRoles.PlatformAdmin))
   ```
   One policy for reads and writes: the management surface is operator-only, and
   `WriteIncidents` cannot be reused because it also grants `SreInvestigator`.
2. `src/SreAiAgent.Contracts/ModelProviders/` — request and response records,
   snake_case through the existing source generator. There is **no** `api_key`
   field anywhere; the create-revision request carries `credential_ref` only.
   Register every top-level request, response and returned `IReadOnlyList<T>` in
   `Contracts/Serialization/SreAiAgentJsonContext.cs` with its own
   `[JsonSerializable(typeof(...))]`; an unregistered root fails at runtime, not at
   build time.
3. `src/SreAiAgent.Api/Endpoints/ModelProviderEndpoints.cs`, mapped from
   `Program.cs` after `MapApprovalEndpoints()`, group `/v1`, tag `Model providers`,
   every route `.RequireAuthorization(SrePolicies.ManageModelProviders)`:

   | Method | Route | Control-plane call |
   |---|---|---|
   | GET | `/v1/model-providers` | `ListConnectionsAsync` |
   | POST | `/v1/model-providers` | `CreateConnectionAsync` |
   | GET | `/v1/model-providers/{connectionId:guid}/revisions` | `ListRevisionsAsync` |
   | POST | `/v1/model-providers/{connectionId:guid}/revisions` | `CreateRevisionAsync` |
   | POST | `/v1/model-providers/{connectionId:guid}/revisions/{revision:int}/verify` | `VerifyRevisionAsync` |
   | GET | `/v1/model-deployments` | `ListDeploymentVersionsAsync`, optional `?logical_name=` filter applied in the endpoint |
   | POST | `/v1/model-deployments` | `CreateDeploymentVersionAsync` |
   | POST | `/v1/model-deployments/{deploymentVersionId:guid}/versions` | `CreateNextDeploymentVersionAsync` |
   | GET | `/v1/model-assignment-releases` | `ListAssignmentReleasesAsync` |
   | POST | `/v1/model-assignment-releases` | `CreateAssignmentReleaseAsync` |
   | POST | `/v1/model-assignment-releases/{releaseId:guid}/evaluate` | `RequestEvaluationAsync` |
   | POST | `/v1/model-assignment-releases/{releaseId:guid}/evaluations/{evaluationId:guid}` | `CompleteEvaluationAsync` (S5.8) |
   | POST | `/v1/model-assignment-releases/{releaseId:guid}/activate` | `ActivateAsync` |

   There is no `GET /v1/model-deployments/{id}/versions`: the control plane has no
   logical-deployment identity, and the `logical_name` filter answers the same
   question without inventing one.
4. Conventions, decided here so the implementer does not invent them:
   - **Pagination.** Lists return the full tenant-scoped set capped at a
     `DefaultReadLimit = 100` constant on the endpoint class, matching
     `ApprovalEndpoints`. No cursor: the control plane's row counts are
     operator-scale.
   - **Idempotency.** No `Idempotency-Key` header. Every create is already unique by
     `(tenant, display_name)`, `(tenant, logical_name, version)` or
     `(tenant, name, version)`; a repeat returns `MODEL_RESOURCE_CONFLICT` (409)
     through S5.6.
   - **Concurrency.** No `ETag`/`If-Match`. Every mutable thing is the active-release
     pointer, and it is only ever set by `activate` on an immutable release id, so a
     lost update is not expressible.
   - **Status codes.** `POST` create returns 201 with the resource;
     `verify`/`evaluate`/`evaluations/{id}`/`activate` return 200; everything else
     flows through `ToHttpResult`, so `ErrorCatalog` decides.
5. Never accept tenant from route, body or header — `principal.Tenant()` only, and
   pass it into every control-plane call. Do **not** copy
   `IncidentEndpoints.InvestigateAsync`/`RetryAsync` or
   `ApprovalEndpoints.ApproveAsync`, which load by id without a tenant filter.
6. New `tests/SreAiAgent.SecurityTests/ModelProviderAuthorizationTests.cs` — an
   `SreInvestigator` principal is refused on every management route; an `SreOperator`
   is admitted; a request whose body names another tenant still resolves the
   principal's tenant.

### S9 — Telemetry and the decision record

1. `src/SreAiAgent.Application/Diagnostics/SreMetrics.cs` — `LlmCall` tags only
   provider kind and status while the instrument description promises "by provider,
   model and outcome". Add `deploymentLogicalName` and `deploymentVersion`
   parameters and tags; do **not** add tenant (unbounded cardinality). Update the
   two call sites in `LlmGateway` (`Interpret` success path and `Fail`).
2. Any new span attribute must be added as a constant **and** to
   `TraceAttributes.Allowed`, or it is dropped and only
   `sre_trace_attribute_rejected_total` moves. Add
   `sre.model.deployment_version` and `sre.model.assignment_release` there, set them
   with `SetSafeTag` in `LlmGateway`, and extend
   `tests/SreAiAgent.SecurityTests/SpanAttributeLeakTests.cs` to assert both are
   recorded. Nothing carrying a prompt, a response body or a credential is
   admissible.
3. `docs/DECISIONS.md` — append, continuing from D32:
   - **D33 — Model identity is a tenant-owned immutable release, not a profile name.**
     Cost: two model systems during the cutover, and the shipped `config/models`
     artifacts now seed rather than resolve.
   - **D34 — The bootstrap seed writes the active pointer directly.** It bypasses the
     verify/evaluate gate because there is no operator at first start; the cost is
     that a wrong `Llm:Bootstrap` endpoint becomes active, which is why Production
     verifies over the network and fails startup.
   - **D35 — Immutability is a database trigger, not an interceptor.** Cost: raw SQL
     in a migration and a `Down` that must drop it; the benefit is that a psql
     session cannot rewrite a published revision.
   - **D36 — The gateway owns the retry budget; the provider HTTP client has no
     resilience handler and no timeout.** Cost: an unbounded `HttpClient` deadline
     that only the linked cancellation token closes.
4. Update `docs/agents/MAP.md`'s composition-root table row for `AddSreLlm`, and
   `ARCHITECTURE.md` section 8.2, to describe assignment-release resolution instead
   of global profiles.

## Critical files & anchors

- `src/SreAiAgent.Infrastructure/DependencyInjection.cs:105-128` — `AddSreLlm`; the
  single blocker that makes the whole graph unconstructible.
- `src/SreAiAgent.Infrastructure/Llm/Providers/ProviderClientResolver.cs` —
  `ProviderClientCatalog.Create` / `Descriptor` and `ChatClientResolver`'s cache
  validation; both S1 and S2 edit it.
- `src/SreAiAgent.Infrastructure/Persistence/Configurations/ModelProviderConfigurations.cs`
  — every table, index and FK; must be correct **before** the migration is generated.
- `src/SreAiAgent.Infrastructure/Llm/ModelProviderControlPlane.cs` — all of S5's
  correctness gaps live here.
- `tests/SreAiAgent.Evals/Harness/ProductionEvalRuntime.cs` — the stale consumer;
  S7 rewrites it around `InvestigationActivities`.

## Verification

Working directory is the repository root. Docker is required from step 3 onward.

1. `dotnet build SreAiAgent.slnx -warnaserror` — zero errors. Expect new errors in
   `tests/SreAiAgent.Evals` the first time Infrastructure compiles; they are S7's.
2. `bash eng/ci.sh` (or `pwsh -File eng/ci.ps1`) — restore `--locked-mode`, format,
   build, unit suites and the deterministic scripted eval smoke. The smoke must
   write `artifacts/evals/<run-id>/manifest.json`, `attempts.jsonl`, `summary.json`
   and `results.junit.xml`, and `ArtifactWriter.EnsureSafe` must not throw.
3. `bash eng/ci.sh --with-integration` — adds the Testcontainers suites, including
   `ModelControlPlaneTests`. This is the only proof the migration applies and the
   immutability triggers bind.
4. New-behaviour check, end to end, following `docs/agents/recipes/verify-end-to-end.md`:
   bring up the compose stack, POST an alert, and confirm from the database that the
   incident's `configuration_snapshots.model_assignment_json` names the bootstrap
   assignment release id and that each `llm_calls` row for the investigation carries
   the same `assignment_release_id`, a non-empty `deployment_configuration_hash`, and
   `observed_provider = 'scripted'`. That single query is what proves exact pinning
   rather than a profile name.
5. Second new-behaviour check, management API, against the running stack with an
   operator token:
   - `POST /v1/model-providers` `{"display_name":"acme-openai"}` → 201.
   - `POST /v1/model-providers/{id}/revisions` with
     `{"adapter_kind":"openai","endpoint":"https://api.openai.com/v1/","credential_ref":"sk-live-abc","declared_capabilities":["structured_output"]}`
     → **400 `MODEL_CONFIGURATION_INVALID`**, and `SELECT count(*) FROM
     provider_connection_revisions` is unchanged. This is the S5.1 raw-secret guard;
     it is the single most important assertion in this plan.
   - the same request with `"credential_ref":"env://OPENAI_API_KEY"` → 201, and the
     stored row's `credential_ref` is the reference, never a key.
   - `POST /v1/model-assignment-releases/{id}/activate` before any evaluation →
     409 `MODEL_ASSIGNMENT_EVALUATION_REQUIRED`; after
     `POST .../evaluations/{evaluationId}` with `{"passed":true,...}` → 200, and
     `tenant_active_model_assignments` points at the new release while the previous
     release row is untouched.
   - `UPDATE provider_connection_revisions SET endpoint='x'` in psql → the trigger
     raises `MODEL_CONFIGURATION_INVALID`.
6. Live provider matrix, only when a real credential is available:
   `bash eng/ci.sh --live-eval` with `SRE_EVAL_CANDIDATE`, `SRE_EVAL_BASELINE` and
   the candidate's credential environment variable set. Confirm it **fails loudly**
   when the credential variable is unset — a requested live run that skips is the
   failure mode the harness exists to prevent.
7. `grep`-equivalent audit before committing: no `api_key`, no `Bearer `, no
   `sk-` prefix in `artifacts/evals/**`, in any span attribute constant, or in
   audit-writing/configuration source paths.

## Assumptions & contingencies

- **The uncommitted work is wanted as written.** This plan repairs and completes it
  rather than reverting. If any of it should instead be discarded, say so before S1;
  after S4 the migration makes reverting expensive.
- **`Llm:Bootstrap` is acceptable as the seeding mechanism.** If a seeded assignment
  in Development is unwanted, the fallback is to require `POST /v1/model-providers`
  before any incident can be opened — then S3 shrinks to nothing, S8 must land
  before the stack is usable at all, and `verify-end-to-end.md` grows a provisioning
  preamble. The seeded default is chosen because the compose stack, the integration
  suite and the eval harness all need a working assignment with no operator present.
- **The deterministic scripted adapter stays the CI model.** If a real provider
  should run in PR CI, the live matrix in `eng/ci.sh --live-eval` already exists;
  only the credential provisioning is missing.
- **Phase 4 and phase 5 of `docs/plans/llm-providers.md` are the next increment,**
  not this one: the versioned baseline promotion gate with statistical comparison,
  the calibrated LLM judge, secret rotation, provider health checks and the
  full-stack multi-provider rollback drill. Record them as open in the `## Durum`
  block of `docs/plans/llm-providers-resume.md` so the next session starts from the
  file rather than from a rediscovery pass.
- **If `dotnet ef migrations add` produces a migration that also drops or recreates
  an unrelated table,** the cause is an entity configuration change that slipped in
  with the uncommitted work; diff the generated file against
  `20260827120553_AuditTrail.cs` and fix the configuration rather than editing the
  generated migration.
