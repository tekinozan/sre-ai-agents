> **Status — 2026-09-10:** planned; no step has been executed. `tests/SreAiAgent.ApiTests`
> does not exist yet and no test project references `Microsoft.AspNetCore.Mvc.Testing`.
> **Scope:** the ADP-003 rows of `adp-003-integration-support-and-testing.md`, plus the
> ADP-005 triage decision. Sequencing lives in `adp-phased-execution-plan.md` (phase F0).
> **Out of scope:** product/environment scope (ADP-101–105), new provider adapters,
> live-provider runs, and any release-support promotion.

# ADP-003 evidence closure — HTTP boundary proof for alert ingress and model-provider management

## Context

`docs/plans/adp-003-integration-support-and-testing.md` (Turkish, committed) declares three verdicts
per integration — *uygulandı* (implemented), *doğrulandı* (verified by an automated test),
*ilk sürümde desteklenir* (release-gated) — and today marks almost every row **bilinmiyor**
(unknown) because the assertions it demands at the "HTTP integration" layer have no home:
the repository has **no HTTP-level test host at all**.

Verified this session:

- `src/SreAiAgent.Api/Program.cs:123-124` already ends with
  `/// <summary>Entry point marker, so the integration suite can host this assembly.</summary>`
  followed by `public partial class Program;` — the hook for a host that was never built.
- `Directory.Packages.props:112` already pins
  `<PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="$(MicrosoftExtensionsVersion)" />`,
  and **no** test project references it: `grep WebApplicationFactory|TestServer|Mvc.Testing` over
  `tests/` returns no matches.
- Consequently these ADP-003 rows are unprovable today (scout-verified as ABSENT):
  authorized ingress, missing/invalid JWT, insufficient policy, duplicate submission.
  Only the pure-unit rows are covered:
  `tests/SreAiAgent.Infrastructure.Tests/AlertTenantBindingTests.cs:33,42,51` and
  `tests/SreAiAgent.Infrastructure.Tests/AlertAdapterTests.cs:58,144,181`.

End state of this plan: a new Docker-free `tests/SreAiAgent.ApiTests` project hosts the API
in-process and proves the authentication/authorization/tenant-scope boundary on every default CI
run; persistence-visible side-effect assertions (outbox row written / not written) land in the
existing `tests/SreAiAgent.IntegrationTests`; the ADP-003 inventory and the ADP-003 row of
`docs/plans/product-adaptability-implementation-and-testing.md` are updated from *bilinmiyor* to
*doğrulandı* **only** for rows a named new test actually proves; and the adapter-vs-config triage
(ADP-005) is recorded as a decision in `docs/DECISIONS.md`.

Scope boundary: no product/environment scope dimension is introduced (that is ADP-101–105, a
separate plan), no new provider adapter is added, and no live-provider run is performed.

## Language of edits

The two ADP documents are Turkish; every row or sentence added to them MUST be Turkish and match
the surrounding column count and prose width. Code, comments, test names, commit messages and the
`docs/DECISIONS.md` entry are English, matching the rest of the repository.

## Verified baseline (do not re-litigate)

| Fact | Evidence |
|---|---|
| Alert ingress route and policy | `src/SreAiAgent.Api/Endpoints/AlertEndpoints.cs:34-35` — `MapPost("/v1/alerts/{source}", IngestAsync)` + `.RequireAuthorization(SrePolicies.WriteIncidents)` |
| Tenant comes from the principal | `AlertEndpoints.cs:59` `context.User.Tenant()`; `src/SreAiAgent.Api/Security/SecurityDependencyInjection.cs:108-113` reads claim `SreClaims.Tenant` |
| Payload-vs-caller tenant conflict | `src/SreAiAgent.Infrastructure/Alerts/AlertNormalizer.cs:83` `public static Result<CreateIncidentRequest> BindTenant(CreateIncidentRequest request, string tenant)`; conflict → `Error.Permanent(ApplicationErrorCodes.Forbidden, …)` at `:95-103`; missing label → caller tenant injected at `:105-111` |
| Management surface exists, tenant-scoped | `src/SreAiAgent.Api/Endpoints/ModelProviderEndpoints.cs:32-82`, 13 routes, all `.RequireAuthorization(SrePolicies.ManageModelProviders)` |
| Control plane is tenant-only | `src/SreAiAgent.Application/Abstractions/Llm/IModelProviderControlPlane.cs:49-66` — every one of the 14 members takes `string tenant`; no product/environment dimension exists anywhere |
| Authorization policies | `SecurityDependencyInjection.cs:71-91` — `ReadIncidents`, `WriteIncidents` (Investigator/Operator/PlatformAdmin), `DecideApprovals`, `ManageModelProviders` (Operator/PlatformAdmin) |
| Query allowlist already enforced | `src/SreAiAgent.Infrastructure/Observability/Providers/Shared/PromQuery.cs:75,84,161` throw `ObservabilityQueryException`; `environment` and `service` are platform-reserved labels |
| Live eval gate exists | `eng/ci.sh --live-eval` / `eng/ci.ps1 -LiveEval`, trait `Category=LiveEval`, env `SRE_EVAL_CANDIDATE`, `SRE_EVAL_BASELINE` (or `SRE_EVAL_WRITE_BASELINE=1`) |
| Decisions log | `docs/DECISIONS.md` currently holds D01–D36; next id is **D37** |

## Approach

### 1. Scaffold `tests/SreAiAgent.ApiTests` so the default gate picks it up

Nothing else compiles until this exists. Four edits plus one restore:

- Create `tests/SreAiAgent.ApiTests/SreAiAgent.ApiTests.csproj`. Everything else (xunit.v3,
  Shouldly, NSubstitute, coverlet, `OutputType=Exe`, `IsTestProject`, global usings) is inherited
  from `tests/Directory.Build.props`; do not restate it:

  ```xml
  <Project Sdk="Microsoft.NET.Sdk">

    <ItemGroup>
      <ProjectReference Include="..\..\src\SreAiAgent.Api\SreAiAgent.Api.csproj" />
    </ItemGroup>

    <ItemGroup>
      <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
    </ItemGroup>

  </Project>
  ```

  No `Version` attribute: central package management already pins it at `Directory.Packages.props:112`.
- Add `<InternalsVisibleTo Include="SreAiAgent.ApiTests" />` to the `Test visibility` group of
  `src/SreAiAgent.Infrastructure/SreAiAgent.Infrastructure.csproj` (after line 25). Required because
  `IMessageProducer` is `internal` (`src/SreAiAgent.Infrastructure/Messaging/IMessageProducer.cs:16`)
  and the host must substitute it. `DynamicProxyGenAssembly2` is already listed at line 26, so
  NSubstitute can proxy it.
- Add the solution entry as the **first** child of `<Folder Name="/tests/">` in `SreAiAgent.slnx`
  (entries are alphabetical by path, 4-space indent):
  `    <Project Path="tests/SreAiAgent.ApiTests/SreAiAgent.ApiTests.csproj" />`
- Add `tests/SreAiAgent.ApiTests/RepositoryPaths.cs`: a byte-for-byte copy of
  `tests/SreAiAgent.Infrastructure.Tests/RepositoryPaths.cs` with namespace `SreAiAgent.ApiTests`.
  Every test project keeps its own copy (Infrastructure.Tests, SecurityTests, ContractTests and
  Evals each have one); do not "fix" this by hoisting it into `SreAiAgent.TestKit`.
- Generate the committed lock file: `dotnet restore SreAiAgent.slnx --force-evaluate`. A plain
  restore reuses the old lock and reports success while the new project has none.

No change to `eng/ci.sh` or `eng/ci.ps1`: the default gate runs `dotnet test "$SOLUTION"` with
`--filter-not-trait "Category=Integration"` / `ScriptedEval` / `LiveEval`, so an untraited project in
the solution runs automatically. The `Category=Integration` loops at `eng/ci.sh:51` and
`eng/ci.ps1:73` are named-project lists and MUST NOT gain `SreAiAgent.ApiTests` — this suite needs
no Docker.

### 2. Mint real tokens through the production JWT path

`tests/SreAiAgent.ApiTests/TestTokens.cs`, `internal static class TestTokens`:

- `public const string Issuer = "https://sre-ai-agent.tests";`
- `public const string Audience = "sre-ai-agent-tests";`
- `SigningKey` — `SymmetricSecurityKey` over a 32-byte ASCII constant declared once here.
- `public static string For(string tenant, params string[] roles)` — builds a token with
  `SreClaims.Tenant` (`"tenant"`), `ClaimTypes.NameIdentifier = "test-operator"` and one
  `ClaimTypes.Role` per role, signed `HmacSha256`, lifetime 5 minutes, via
  `JsonWebTokenHandler.CreateToken(new SecurityTokenDescriptor { ... })`.

Development authentication is deliberately **not** used: `DevelopmentAuthenticationHandler` emits a
fixed principal (tenant `"local"`, roles Viewer/Investigator/Operator/Approver, never PlatformAdmin)
and therefore cannot express an unauthenticated caller, a wrong-role caller, or tenant B. Signing
real tokens exercises the same `AddJwtBearer` pipeline production uses.

### 3. The Docker-free host fixture

`tests/SreAiAgent.ApiTests/SreApiFactory.cs` — `public sealed class SreApiFactory : WebApplicationFactory<Program>`.

`UseEnvironment("Testing")`: `Development` is wrong because `src/SreAiAgent.Api/appsettings.Development.json`
sets `Database:MigrateOnStartup: true` and an `Llm:Bootstrap` block, both of which make startup open a
PostgreSQL connection; `Production` is wrong because it forbids the inline workflow engine and fake
observability providers. `appsettings.json` supplies the release ids, `Observability:*: "fake"` and
`Llm:Provider: "scripted"` unchanged.

Override exactly these keys through an in-memory configuration source (each one is load-bearing):

| Key | Value | Why |
|---|---|---|
| `Database:ConnectionString` | `Host=127.0.0.1;Port=5432;Database=sre_apitests;Username=apitests;Password=apitests` | `[Required]`; `appsettings.json:11` ships empty, so validation fails at start. Never connected to. |
| `Database:MigrateOnStartup` | `false` | `DatabaseMigrator` is a hosted service; `true` calls `MigrateAsync` at boot. |
| `Registries:ConfigurationRoot` | `RepositoryPaths.Config` | `RegistryValidator` (hosted) loads the shipped YAML; the default `"config"` resolves under the test `bin/`. |
| `Registries:PromptRoot` | `RepositoryPaths.Prompts` | Same. |
| `Workflow:Engine` | `inline` | `appsettings.json:63` says `temporal`; inline needs no Temporal server and is legal outside Production. |
| `Outbox:Enabled` | `false` | `OutboxDispatcher.ExecuteAsync` returns immediately, so nothing drains the staged messages the tests assert on. |
| `Security:AllowDevelopmentAuthentication` | `false` | Keeps the JWT branch. |
| `Security:Audience` | `TestTokens.Audience` | Bound eagerly in `Program.cs:40-43`. |

In `ConfigureTestServices`:

- `PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, …)` and set
  `Authority = null`, `MetadataAddress = null`, `RequireHttpsMetadata = false`,
  `Configuration = new OpenIdConnectConfiguration()` (suppresses discovery, which would otherwise
  HTTP-GET `/.well-known/openid-configuration` on the first authenticated request) and a full
  `TokenValidationParameters` with `IssuerSigningKey = TestTokens.SigningKey`,
  `ValidIssuer = TestTokens.Issuer`, `ValidAudience = TestTokens.Audience`,
  `RoleClaimType = ClaimTypes.Role`, `NameClaimType = ClaimTypes.NameIdentifier`,
  `ClockSkew = TimeSpan.FromSeconds(30)` — mirroring `SecurityDependencyInjection.cs:42-67`.
- Replace four services. `IEventPublisher` and `IUnitOfWork` are registered `TryAddScoped` in
  `PersistenceDependencyInjection.cs:109,119` against `SreDbContext`, so both must go:

  ```csharp
  services.RemoveAll<IEventPublisher>();
  services.AddSingleton<RecordingEventPublisher>();
  services.AddScoped<IEventPublisher>(sp => sp.GetRequiredService<RecordingEventPublisher>());
  ```

  Same shape for `IUnitOfWork` → `CountingUnitOfWork`, plus
  `services.RemoveAll<IModelProviderControlPlane>()` and `services.RemoveAll<IMessageProducer>()`
  replaced by NSubstitute singletons. The substitutes and recorders are exposed as factory
  properties `Publisher`, `UnitOfWork`, `ControlPlane`.
- `public void Reset()` clears the recorder, zeroes the save counter and calls
  `ControlPlane.ClearReceivedCalls()`. Suites take the factory as `IClassFixture<SreApiFactory>` and
  call `Reset()` first in every test, so host startup (YAML registry validation) happens once.

`tests/SreAiAgent.ApiTests/TestDoubles.cs`:

- `RecordingEventPublisher : IEventPublisher` — implements both overloads declared in
  `src/SreAiAgent.Application/Abstractions/Messaging/IEventPublisher.cs:22,28` and appends
  `(topic, eventType, payload, correlationId)` to a list. The generic overload is the one
  `AlertEndpoints` uses.
- `CountingUnitOfWork : IUnitOfWork` — `SaveChangesAsync` increments a counter and returns `0`;
  `ExecuteInTransactionAsync` invokes the operation directly.

### 4. Prove the ingress boundary without Docker

`tests/SreAiAgent.ApiTests/AlertIngressHttpTests.cs`. Payload bodies: reuse the Alertmanager JSON
shape already exercised by `tests/SreAiAgent.Infrastructure.Tests/AlertAdapterTests.cs` rather than
inventing one. Route `POST /v1/alerts/alertmanager` unless stated. Every case also asserts the
recorder is untouched on refusal — a rejected alert that still staged a message is the bug worth
catching.

| Test | Input | Expected |
|---|---|---|
| `AnAuthorizedAlertIsAcceptedAndStagedOnce` | token `tenant-a` + role `SreRoles.Investigator` | 202; exactly one recorded message, topic `KafkaTopics.AlertReceived`, event type `"sre.alert.received"`, payload `CanonicalAlert.Labels["tenant"] == "tenant-a"`; save count 1 |
| `AnAlertWithoutATokenIsRefusedBeforeAnythingIsStaged` | no `Authorization` header | 401; zero recorded messages; save count 0 |
| `AViewerMayNotIngestAlerts` | token `tenant-a` + role `SreRoles.Viewer` | 403; zero recorded; save count 0 |
| `APayloadNamingAnotherTenantIsRefusedWithForbidden` | token `tenant-a`, payload label `tenant=tenant-b` | 403; ProblemDetails `code` extension `"FORBIDDEN"`; zero recorded |
| `ACallerWithNoTenantClaimIsRefused` | token signed with roles but no `tenant` claim | 403 (policy requires the claim); zero recorded |
| `AnUnconfiguredSourceIsRefused` | `POST /v1/alerts/nope`, valid token | 400; `code` `"VALIDATION_FAILED"`; zero recorded |
| `AMalformedPayloadIsRefused` | Alertmanager payload with no service label | 400; `code` `"VALIDATION_FAILED"`; zero recorded |

`tests/SreAiAgent.ApiTests/ModelProviderAuthorizationTests.cs`:

| Test | Input | Expected |
|---|---|---|
| `TheManagementSurfaceUsesTheTenantFromTheToken` | `GET /v1/model-providers`, token `tenant-a` + `SreRoles.Operator`; substitute returns an empty list | 200; substitute received `ListConnectionsAsync("tenant-a", Arg.Any<CancellationToken>())` exactly once |
| `AnInvestigatorMayNotManageModelProviders` | same route, role `SreRoles.Investigator` | 403; substitute received no calls |
| `ManagementRoutesRejectAnAnonymousCaller` | same route, no token | 401; substitute received no calls |

`tests/SreAiAgent.ApiTests/HostBootTests.cs`: `TheHostAnswersLiveWithoutAnyBackend` — `GET /health/live`
returns 200 with no database, broker or Temporal running. This is the guard that keeps the fixture
honest: if a future registration starts dialling a backend at boot, this fails first and cheaply.

### 5. Prove the persisted side effect with a real database

In `tests/SreAiAgent.IntegrationTests` (Docker gate). Every new class MUST carry
`[Trait("Category", "Integration")]` or `SuiteConventionTests.EveryIntegrationSuiteIsTraited`
(`tests/SreAiAgent.IntegrationTests/SuiteConventionTests.cs:19-35`) fails.

- Add to `tests/SreAiAgent.IntegrationTests/SreAiAgent.IntegrationTests.csproj`:
  `<ProjectReference Include="..\..\src\SreAiAgent.Api\SreAiAgent.Api.csproj" />` and
  `<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />`, then re-run the
  `--force-evaluate` restore from step 1 (do both csproj edits before restoring, so one restore
  regenerates both lock files).
- `SreApiPostgresFactory.cs` — same `WebApplicationFactory<Program>` shape and the same JWT
  `PostConfigure` (copy `TestTokens` into this project as well; the two fixtures are deliberately
  separate because one proves the boundary with doubles and the other proves persistence with real
  adapters — do not merge them into `SreAiAgent.TestKit`), but: `Database:ConnectionString` comes
  from `PostgresFixture.ConnectionString`, `Database:MigrateOnStartup` stays `false` (the fixture
  migrates once in `InitializeAsync`), `Outbox:Enabled` stays `false`, and only `IMessageProducer`
  is substituted. `IEventPublisher`, `IUnitOfWork` and `IModelProviderControlPlane` stay production.
- `AlertIngressPersistenceTests.cs`, `[Collection(PostgresCollection.Name)]`:
  - `AnAcceptedAlertLeavesExactlyOneOutboxRow` — POST as `tenant-a`, expect 202, then read
    `SreDbContext.OutboxMessages` through `PostgresFixture.CreateContext()`: exactly one row for
    topic `KafkaTopics.AlertReceived` whose payload carries `"tenant":"tenant-a"`.
  - `ARefusedCrossTenantAlertLeavesNoOutboxRow` — token `tenant-a`, payload label `tenant-b`,
    expect 403 and zero rows. Delete the rows the first test created (or filter by fingerprint) so
    the suite stays order-independent, matching how the existing suites clean up after themselves.
- `ModelProviderTenantHttpTests.cs`, `[Collection(PostgresCollection.Name)]`:
  - `AConnectionOfAnotherTenantIsNotFoundOverHttp` — create a connection for `tenant-a` through the
    host's own `IModelProviderControlPlane` (`factory.Services.CreateScope()`), then
    `GET /v1/model-providers/{connectionId}/revisions` with a `tenant-b` operator token: expect 404
    and `code` `"PROVIDER_CONNECTION_NOT_FOUND"` (`ErrorCatalog.cs:44` maps it to 404). 404 rather
    than 403 is the intended contract — `ResultExtensions.cs:40-45` explains that a 403 would
    confirm the row exists.

### 6. Update the two ADP documents to the evidence, and nothing beyond it

All prose Turkish, column counts unchanged.

- `docs/plans/adp-003-integration-support-and-testing.md`
  - Inventory row 31 (`| Alert ingress | …`): `Doğrulandı` column becomes
    `Unit, HTTP ve persistence testleri`; the evidence column names the new suites
    (`AlertIngressHttpTests`, `AlertIngressPersistenceTests`) with the run date. `İlk sürüm desteği`
    stays `Bilinmiyor`: this plan produces no release evidence record.
  - Rows 26-30 and 32 are untouched. Provider rows stay `Bilinmiyor` — no live provider ran.
  - In the `## Alert ingress ve tenant sınırı` table (rows 103-108), append the proving test name to
    each row this work covers: authorized ingress, missing/invalid JWT and insufficient policy,
    JWT-vs-payload tenant conflict, missing tenant label, unknown source and malformed payload.
    The `Aynı tenant tekrar gönderimi` row stays unproven at HTTP level and MUST say so, pointing at
    `tests/SreAiAgent.IntegrationTests/IncidentDeduplicationTests.cs` as database-level evidence
    instead of implying an HTTP idempotency contract that does not exist.
  - Lines 110-112 currently claim the suite uses a *mevcut* in-memory publisher; replace with the
    two doubles actually introduced (`RecordingEventPublisher`, `CountingUnitOfWork`) and the signed
    test token, since no in-memory publisher existed before this work.
  - In `## ADP-003 sonrası ayrı işler` (lines 194-197) remove `eksik HTTP integration host veya fake`
    from the backlog list — it is landed — and leave capability verification, the live provider
    matrix, datasource contract coverage, runbook and CI job items intact.
- `docs/plans/product-adaptability-implementation-and-testing.md`
  - Line 103: state that ADP-003 now carries evidence while ADP-001, ADP-002, ADP-004 and ADP-005
    remain `planlandı`.
  - Line 110 (`| ADP-003 | …`): note the landed HTTP boundary evidence.
  - Lines 134-135 (`Giriş: yok. Çıkış/kabul: …`): Aşama 0 is **not** complete. ADP-004 needs two real
    product/environment targets that nobody has supplied, and the document forbids inventing names,
    so the acceptance sentence must record ADP-003 as met and ADP-004 as blocked pending real
    targets.
- `docs/plans/README.md:9` — English, matching the surrounding rows: the ADP-003 row's status becomes
  the HTTP/persistence boundary evidence landing, with live-provider and release evidence pending.
- `docs/DECISIONS.md` — append `### D37 — …` in the exact heading shape of the existing last entry
  (D36; there is no table of contents to update). Record the decision that HTTP boundary proof is
  split across two suites — an untraited `SreAiAgent.ApiTests` so every default gate run proves the
  auth/tenant boundary, and `Category=Integration` suites for anything a real database must show —
  and its cost: two `WebApplicationFactory` fixtures and a duplicated test-token helper, which is
  the price of not putting the security assertions behind a Docker daemon.

## Critical files & anchors

| File | Anchor | Why it decides something |
|---|---|---|
| `src/SreAiAgent.Api/Program.cs` | `:20-35` composition root, `:123-124` `public partial class Program` | The host under test; the marker comment is the contract the factory relies on |
| `src/SreAiAgent.Infrastructure/PersistenceDependencyInjection.cs` | `:98-125` | Names the two hosted services and the `TryAddScoped` ports the fixture must displace |
| `src/SreAiAgent.Api/Security/SecurityDependencyInjection.cs` | `:35-67` dev-vs-JWT branch, `:71-91` policies | The exact validation parameters and role sets the tests target |
| `src/SreAiAgent.Api/Endpoints/AlertEndpoints.cs` | `:43-80` | The ordered failure branches each assertion pins |
| `tests/SreAiAgent.IntegrationTests/PostgresFixture.cs` | `:22-58`, `:78-82` | Migrated-once container and the `"postgres"` collection the new suites join |

## Verification

Prerequisites: .NET SDK per `global.json`; a Docker daemon for the integration gate only; no
environment variables and no live-provider credentials (nothing here is a live run).

1. `dotnet restore SreAiAgent.slnx --force-evaluate` — must produce
   `tests/SreAiAgent.ApiTests/packages.lock.json` and update
   `tests/SreAiAgent.IntegrationTests/packages.lock.json`.
2. Default gate, which is where the new security proof has to appear:
   `bash eng/ci.sh` (Windows: `powershell -ExecutionPolicy Bypass -File eng/ci.ps1`).
   Expect exit 0, zero warnings, and the total test count up by the 11 new untraited tests.
3. New-behaviour check with a concrete input and observable output, run on its own:
   `dotnet test tests/SreAiAgent.ApiTests/SreAiAgent.ApiTests.csproj --no-build`
   - `POST /v1/alerts/alertmanager`, `Authorization: Bearer <token tenant=tenant-a role=SreInvestigator>`,
     Alertmanager body whose labels contain `tenant=tenant-b` → **HTTP 403**, ProblemDetails
     `code == "FORBIDDEN"`, recorded messages **0**, save count **0**.
   - The same request without the payload tenant label → **HTTP 202**, recorded messages **1**,
     canonical `Labels["tenant"] == "tenant-a"`.
   Both must be observed passing; the first one failing with 202 would mean the tenant guard is not
   reached over HTTP, which is the exact defect this suite exists to detect.
4. Integration gate: `bash eng/ci.sh --with-integration` (Windows: `pwsh -File eng/ci.ps1 -WithIntegration`).
   Expect exit 0 and, in `SreAiAgent.IntegrationTests`, the accepted alert leaving exactly one
   outbox row and the cross-tenant `GET` answering 404 `PROVIDER_CONNECTION_NOT_FOUND`.
5. Re-read the two ADP documents after editing and confirm no row claims `İlk sürüm desteği` and no
   provider row moved off `Bilinmiyor`. A row upgraded without a named test and date is the failure
   mode ADP-003 was written to prevent.

## Assumptions & contingencies

- **Transitive JWT types.** `Microsoft.IdentityModel.JsonWebTokens` 8.19.2 arrives through the Api
  project reference (`src/SreAiAgent.Api/packages.lock.json:164-166`). If `JsonWebTokenHandler` or
  `OpenIdConnectConfiguration` will not resolve at compile time, add
  `<PackageVersion Include="Microsoft.IdentityModel.JsonWebTokens" Version="8.19.2" />` to the
  `ASP.NET Core` group of `Directory.Packages.props`, reference it from both test projects, and
  re-run the `--force-evaluate` restore. Do not hand-edit a lock file.
- **Content root.** If `WebApplicationFactory<Program>` resolves the content root to the test output
  directory and `appsettings.json` is therefore not read, set it explicitly with
  `builder.UseSetting(WebHostDefaults.ContentRootKey, Path.Combine(RepositoryPaths.Root, "src", "SreAiAgent.Api"))`
  rather than copying settings into the test project.
- **Adapter rejection code.** The malformed-payload assertion expects `VALIDATION_FAILED`. If
  `src/SreAiAgent.Infrastructure/Alerts/AlertSourceAdapters.cs` returns a different code for a
  missing service label, assert that code — do not weaken the assertion to "some 4xx".
- **ADP-004 stays blocked.** Two real pilot product/environment targets were never supplied and the
  guide forbids inventing names, so no selection record is fabricated; Aşama 0 remains open and
  Aşama 1 is not entered by this plan.
- **No release promotion.** `İlk sürüm desteği` requires redacted release evidence and, for provider
  rows, an opt-in live run (`eng/ci.sh --live-eval` with `SRE_EVAL_CANDIDATE` / `SRE_EVAL_BASELINE`).
  Neither is in scope, so every such cell stays `Bilinmiyor`.
