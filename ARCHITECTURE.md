# SRE AI Agent Platform — C# / .NET 10 Mimari Tasarım

> Bu doküman, `sre_ai_agent_enterprise_implementation_plan.md` içindeki hedefleri
> ve güvenlik ilkelerini koruyarak platformu C# ve .NET 10 ile yeniden tasarlar.
> Amaç, Python/LangGraph yapısını satır satır taşımak değil; aynı ürün
> davranışını .NET'in güçlü tip sistemi, Generic Host, ASP.NET Core,
> EF Core ve üretime hazır gözlemlenebilirlik altyapısıyla daha sürdürülebilir
> biçimde kurmaktır.

---

## 1. Önerilen hedef mimari

### 1.1 Ana karar

İlk sürüm **modüler monolith + ayrı worker process'leri** olarak tasarlanır:

```text
                         ┌──────────────────────┐
 Alertmanager/Datadog ──▶│ ASP.NET Core API     │
 Kafka ──────────────────▶│ Alert Ingestion      │
                         └──────────┬───────────┘
                                    │ Outbox / Kafka
                         ┌──────────▼───────────┐
                         │ Investigation Worker │
                         │ Durable Workflow     │
                         └──────────┬───────────┘
                                    │
               ┌────────────────────┼───────────────────┐
               ▼                    ▼                   ▼
        Agent Runtime        Tool Gateway         Evidence Store
               │                    │                   │
               ▼                    ▼                   ▼
             LLM              Policy Engine       PostgreSQL/Object Store
                                    │
       ┌──────────────┬─────────────┼──────────────┬──────────────┐
       ▼              ▼             ▼              ▼              ▼
  Prometheus       Logs          Traces        Kubernetes      DB Metrics
```

API, alert worker ve investigation worker aynı solution ve container image
üzerinde bulunabilir; ancak Kubernetes'te ayrı deployment olarak ölçeklenir.
İlk günden mikroservis sınırlarının getirdiği operasyonel maliyet alınmaz.

### 1.2 Katman kuralı

```text
SreAiAgent.Api / Workers
          │
          ▼
SreAiAgent.Application
          │
          ▼
SreAiAgent.Domain

SreAiAgent.Infrastructure ───────▶ Application
SreAiAgent.Infrastructure ───────▶ Domain
Workflow adapter ────────────────▶ Application
```

`Domain` ve `Application` şu bağımlılıkları alamaz:

- ASP.NET Core
- EF Core veya Npgsql
- Kafka client
- Temporal/Durable Task SDK
- OpenAI/Anthropic provider SDK
- Kubernetes client
- Serilog/OpenTelemetry implementation ayrıntıları

Bu paketler yalnızca `Infrastructure`, `Workflow`, `Api` veya worker
composition root'larında kullanılır.

---

## 2. Python planından .NET 10 karşılıklarına geçiş

| Mevcut plan kavramı | .NET 10 karşılığı |
|---|---|
| FastAPI | ASP.NET Core Minimal APIs |
| Pydantic | `System.Text.Json` + FluentValidation veya boundary validator'ları |
| SQLAlchemy async | EF Core 10 + Npgsql |
| Alembic | EF Core migrations |
| `Protocol` port'ları | C# interface'leri |
| `asyncio` worker | Generic Host + `BackgroundService` |
| Kafka async client | `Confluent.Kafka` adapter'ı |
| LangGraph | Durable workflow adapter; varsayılan öneri Temporal .NET |
| LangGraph checkpoint | Workflow motorunun history/checkpoint'i |
| Jinja2 | Kontrollü prompt renderer; YAML/JSON artifact + token replacement |
| Pydantic structured output | JSON Schema + `System.Text.Json` deserialization/validation |
| pytest | xUnit veya NUnit |
| pytest-asyncio | xUnit async test desteği |
| Testcontainers | `Testcontainers` for .NET |
| Ruff/mypy | `dotnet format`, analyzers, nullable reference types, Roslyn |
| OpenTelemetry Python | .NET OpenTelemetry SDK |
| pydantic-settings | Options pattern + `ValidateOnStart` |
| FastAPI SSE | ASP.NET Core response streaming/SSE |

---

## 3. Workflow motoru kararı

### 3.1 Neden LangGraph port edilmemeli?

LangGraph'ın graph state, reducer ve interrupt kavramlarını .NET'e kopyalamak,
platformun iş kurallarını bir orkestrasyon framework'üne yeniden bağlar.
Asıl ihtiyaç graph çizmekten çok şunlardır:

- process/pod restart sonrası devam edebilme,
- uzun süren investigation'ı güvenilir biçimde yürütme,
- fan-out/fan-in,
- timeout ve cancellation,
- approval sonrasında aynı workflow instance'ını resume etme,
- activity retry ve idempotency.

### 3.2 Önerilen seçim

**Workflow abstraction:**

```csharp
public interface IInvestigationWorkflow
{
    Task RunAsync(
        InvestigationWorkflowInput input,
        CancellationToken cancellationToken);
}
```

**İlk production implementasyonu:** Kubernetes üzerinde self-host edilebilen
Temporal .NET SDK adapter'ı.

Temporal kullanılamayan Azure ağırlıklı kurulumlarda aynı interface için
Durable Task Framework adapter'ı eklenebilir. Application katmanı bu seçimi
bilmez.

Workflow motoru ile business persistence birbirinden ayrıdır:

- Workflow motoru: workflow history, activity state, retry, timer, signal.
- PostgreSQL business store: Incident, Evidence, Finding, RCA, approval ve audit.

`incident_id` workflow instance/business correlation id olarak kullanılır;
ancak business state yalnızca workflow history'ye bırakılmaz.

### 3.3 Workflow state

```csharp
public sealed record InvestigationWorkflowState(
    Guid IncidentId,
    Guid ConfigurationSnapshotId,
    IncidentClassification? Classification,
    IReadOnlyList<string> SelectedAgents,
    IReadOnlyList<AgentFinding> Findings,
    IReadOnlyList<EvidenceId> EvidenceIds,
    RootCauseReport? RootCause,
    IReadOnlyList<Recommendation> Recommendations,
    IReadOnlyList<WorkflowError> Errors,
    InvestigationStatus Status);
```

Bu model `Incident` domain entity'si değildir. Workflow runtime state'i
ile business aggregate ayrı tutulur.

Fan-out/fan-in için mutable shared list yerine activity sonuçları döndürülür;
sonuçlar workflow tarafında deterministik biçimde birleştirilir. Böylece
LangGraph reducer davranışının .NET karşılığı açık ve test edilebilir olur.

---

## 4. Solution ve repository yapısı

```text
sre-ai-platform/
├── SreAiAgent.slnx
├── Directory.Build.props
├── Directory.Packages.props
├── global.json
├── .editorconfig
│
├── src/
│   ├── SreAiAgent.Domain/
│   │   ├── Incidents/
│   │   │   ├── Incident.cs
│   │   │   ├── IncidentStatus.cs
│   │   │   ├── IncidentSeverity.cs
│   │   │   ├── IncidentEvents.cs
│   │   │   └── IncidentStatePolicy.cs
│   │   ├── Evidence/
│   │   ├── Findings/
│   │   ├── Recommendations/
│   │   ├── Remediation/
│   │   ├── Shared/
│   │   │   ├── Entity.cs
│   │   │   ├── DomainEvent.cs
│   │   │   ├── Result.cs
│   │   │   └── StrongIds.cs
│   │   └── SreAiAgent.Domain.csproj
│   │
│   ├── SreAiAgent.Application/
│   │   ├── Abstractions/
│   │   │   ├── Llm/
│   │   │   ├── Persistence/
│   │   │   ├── Messaging/
│   │   │   ├── Workflow/
│   │   │   ├── Tools/
│   │   │   ├── Observability/
│   │   │   └── Security/
│   │   ├── Incidents/
│   │   │   ├── CreateIncident/
│   │   │   ├── GetIncident/
│   │   │   ├── StartInvestigation/
│   │   │   ├── RetryInvestigation/
│   │   │   └── CancelInvestigation/
│   │   ├── Approvals/
│   │   ├── Agents/
│   │   ├── Prompting/
│   │   ├── Tools/
│   │   ├── Workflows/
│   │   └── SreAiAgent.Application.csproj
│   │
│   ├── SreAiAgent.Contracts/
│   │   ├── Incidents/
│   │   ├── Approvals/
│   │   ├── Alerts/
│   │   ├── Streaming/
│   │   └── SreAiAgent.Contracts.csproj
│   │
│   ├── SreAiAgent.Infrastructure/
│   │   ├── Persistence/
│   │   │   ├── SreDbContext.cs
│   │   │   ├── Configurations/
│   │   │   ├── Repositories/
│   │   │   ├── Migrations/
│   │   │   └── Outbox/
│   │   ├── Llm/
│   │   │   ├── OpenAi/
│   │   │   ├── Anthropic/
│   │   │   ├── LlmGateway.cs
│   │   │   └── LlmResilience.cs
│   │   ├── Prompting/
│   │   ├── Agents/
│   │   ├── Tools/
│   │   │   ├── ToolRegistry.cs
│   │   │   ├── ToolGateway.cs
│   │   │   ├── ToolPolicyEngine.cs
│   │   │   └── Adapters/
│   │   ├── Observability/
│   │   │   ├── Prometheus/
│   │   │   ├── Logs/
│   │   │   ├── Traces/
│   │   │   ├── Kubernetes/
│   │   │   └── Database/
│   │   ├── Messaging/
│   │   │   ├── Kafka/
│   │   │   └── Inbox/
│   │   ├── Security/
│   │   └── SreAiAgent.Infrastructure.csproj
│   │
│   ├── SreAiAgent.Workflow/
│   │   ├── Activities/
│   │   ├── Workflows/
│   │   ├── Signals/
│   │   ├── Temporal/
│   │   └── SreAiAgent.Workflow.csproj
│   │
│   ├── SreAiAgent.Api/
│   │   ├── Endpoints/
│   │   ├── Middleware/
│   │   ├── Streaming/
│   │   ├── Program.cs
│   │   └── SreAiAgent.Api.csproj
│   │
│   ├── SreAiAgent.AlertWorker/
│   │   ├── Consumers/
│   │   ├── Normalization/
│   │   ├── Program.cs
│   │   └── SreAiAgent.AlertWorker.csproj
│   │
│   └── SreAiAgent.InvestigationWorker/
│       ├── Consumers/
│       ├── Program.cs
│       └── SreAiAgent.InvestigationWorker.csproj
│
├── tests/
│   ├── SreAiAgent.Domain.Tests/
│   ├── SreAiAgent.Application.Tests/
│   ├── SreAiAgent.Infrastructure.Tests/
│   ├── SreAiAgent.Workflow.Tests/
│   ├── SreAiAgent.ContractTests/
│   ├── SreAiAgent.IntegrationTests/
│   ├── SreAiAgent.SecurityTests/
│   └── SreAiAgent.Evals/
│
├── prompts/
├── config/
├── deploy/
│   ├── docker/
│   ├── helm/
│   └── k8s/
└── docs/
```

`SreAiAgent.Contracts`, API/worker sınırlarında kullanılan DTO'ları taşır.
Domain modelleri dışarıya doğrudan serialize edilmez.

---

## 5. Domain modeli

### 5.1 Strongly typed ID

`Guid` her yerde çıplak kullanılmamalıdır. En azından kritik kimlikler için
strong ID tercih edilir:

```csharp
public readonly record struct IncidentId(Guid Value)
{
    public static IncidentId New() => new(Guid.NewGuid());
}

public readonly record struct EvidenceId(Guid Value);
public readonly record struct AgentRunId(Guid Value);
public readonly record struct RemediationActionId(Guid Value);
```

### 5.2 Incident aggregate

```csharp
public sealed class Incident
{
    private readonly List<IncidentDomainEvent> _events = [];

    private Incident() { }

    public Incident(
        IncidentId id,
        string source,
        string fingerprint,
        string title,
        string service,
        string environment,
        IncidentSeverity severity,
        DateTimeOffset detectedAt)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
            throw new DomainException("INCIDENT_FINGERPRINT_REQUIRED");

        Id = id;
        Source = source;
        Fingerprint = fingerprint;
        Title = title;
        Service = service;
        Environment = environment;
        Severity = severity;
        DetectedAt = detectedAt.ToUniversalTime();
        Status = IncidentStatus.Open;
        CreatedAt = detectedAt.ToUniversalTime();
        UpdatedAt = CreatedAt;
    }

    public IncidentId Id { get; private set; }
    public string Source { get; private set; } = null!;
    public string Fingerprint { get; private set; } = null!;
    public string Title { get; private set; } = null!;
    public string Service { get; private set; } = null!;
    public string Environment { get; private set; } = null!;
    public IncidentSeverity Severity { get; private set; }
    public IncidentStatus Status { get; private set; }
    public DateTimeOffset DetectedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }

    public void StartInvestigation(DateTimeOffset now)
    {
        if (Status is IncidentStatus.Resolved or IncidentStatus.Cancelled)
            throw new InvalidIncidentStateException(Status, "INVESTIGATION_START");

        Status = IncidentStatus.Investigating;
        Touch(now);
        _events.Add(new InvestigationStarted(Id));
    }

    public void MarkResolved(DateTimeOffset now)
    {
        if (Status == IncidentStatus.Cancelled)
            throw new InvalidIncidentStateException(Status, "RESOLVE");

        Status = IncidentStatus.Resolved;
        ResolvedAt = now.ToUniversalTime();
        Touch(now);
        _events.Add(new IncidentResolved(Id));
    }

    public IReadOnlyList<IncidentDomainEvent> DequeueEvents()
    {
        var result = _events.ToArray();
        _events.Clear();
        return result;
    }

    private void Touch(DateTimeOffset now) => UpdatedAt = now.ToUniversalTime();
}
```

Kurallar:

- Resolved incident doğrudan Open'a çevrilmez; reopen ayrı use case'tir.
- Destructive/Write action approval olmadan yürütülemez.
- Confidence her sınırda `0..1` aralığında doğrulanır.
- Evidence referansı olmayan observation `Unverified` olarak işaretlenir
  veya reddedilir.
- Tüm zamanlar UTC'dir.

### 5.3 C# enum'ları

```csharp
public enum IncidentStatus
{
    Open,
    Investigating,
    WaitingApproval,
    Remediating,
    Resolved,
    Failed,
    Cancelled
}

public enum RiskLevel
{
    ReadOnly,
    Low,
    Medium,
    High,
    Destructive
}

public enum EvidenceSourceType
{
    Metric,
    Log,
    Trace,
    Kubernetes,
    Database,
    Deployment,
    Runbook
}
```

---

## 6. Application port'ları

### 6.1 LLM gateway

Provider SDK tipleri application katmanına sızmamalıdır:

```csharp
public interface ILlmGateway
{
    Task<LlmResult<T>> GenerateStructuredAsync<T>(
        LlmRequest<T> request,
        CancellationToken cancellationToken);
}

public sealed record LlmRequest<T>(
    string SystemPrompt,
    string UserPrompt,
    string ResponseSchemaName,
    ModelProfile ModelProfile,
    IReadOnlyList<ToolDefinition> Tools,
    TimeSpan Timeout,
    LlmCallMetadata Metadata);

public sealed record LlmResult<T>(
    T Value,
    string Provider,
    string Model,
    int InputTokens,
    int OutputTokens,
    TimeSpan Latency,
    string RequestId,
    string FinishReason);
```

İç implementasyonda `Microsoft.Extensions.AI` abstraction'ı
kullanılabilir; ancak platformun asıl port'u yine `ILlmGateway` olarak kalır.
Bu, structured output, bütçe, audit ve retry politikalarını provider
abstraction'ından bağımsız tutar.

### 6.2 Tool gateway

```csharp
public interface IToolGateway
{
    Task<ToolExecutionResult> ExecuteAsync(
        ToolExecutionRequest request,
        ToolExecutionContext context,
        CancellationToken cancellationToken);
}

public sealed record ToolExecutionRequest(
    string ToolName,
    string ToolVersion,
    JsonElement Arguments,
    IncidentId IncidentId,
    string AgentName,
    Guid RequestId);

public sealed record ToolExecutionContext(
    string Tenant,
    string Environment,
    IReadOnlySet<string> ServiceAllowlist,
    string Actor,
    RiskLevel RiskCeiling,
    DateTimeOffset Deadline);
```

Gateway sırası:

```text
registry lookup
→ JSON schema validation
→ authorization scope
→ risk ceiling
→ argument/time-window policy
→ secret reference resolution
→ rate limit
→ timeout
→ typed adapter
→ result-size limit
→ redaction
→ Evidence oluşturma
→ audit
→ result
```

LLM hiçbir zaman Prometheus, Kubernetes veya veritabanına doğrudan erişmez.
LLM yalnızca typed `ToolRequest` üretir.

### 6.3 Observability port'ları

```csharp
public interface IMetricsProvider
{
    Task<MetricQueryResult> QueryAsync(
        MetricQuery query, CancellationToken cancellationToken);

    Task<MetricQueryResult> QueryRangeAsync(
        MetricRangeQuery query, CancellationToken cancellationToken);
}

public interface ILogProvider
{
    Task<LogSearchResult> SearchAsync(
        LogSearchQuery query, CancellationToken cancellationToken);
}

public interface ITraceProvider
{
    Task<TraceSearchResult> SearchAsync(
        TraceSearchQuery query, CancellationToken cancellationToken);
}

public interface IKubernetesObservability
{
    Task<PodSummary> GetPodsAsync(
        KubernetesScope scope, CancellationToken cancellationToken);
}

public interface IDatabaseObservability
{
    Task<DatabaseHealth> GetHealthAsync(
        DatabaseScope scope, CancellationToken cancellationToken);
}
```

Application contract'larında PromQL, Elasticsearch DSL veya `kubectl` string'i
bulunmaz. Provider-specific sorgu çevirisi adapter içinde kalır.

---

## 7. Agent runtime ve registries

### 7.1 Configuration-driven agent definition

Agent davranışının çoğu C# class'ı yerine versioned YAML/JSON artifact ile
tanımlanır:

```yaml
name: database-expert
version: v3
enabled: true
prompt_ref: database_expert.investigate:v7
model_profile: analytical:v1
capabilities:
  - database
  - sql
  - connection-pool
incident_types:
  - latency
  - database
  - timeout
tools:
  allow:
    - prometheus.query_range
    - database.connection_stats
    - database.slow_queries
execution:
  max_iterations: 5
  timeout_seconds: 90
  max_tool_calls: 8
  max_llm_calls: 6
output_schema: AgentFinding
```

Startup ve CI aşamasında şu kontroller yapılır:

- prompt referansı var mı,
- model profile var mı,
- tool referansı registry'de var mı,
- output schema yüklenebiliyor mu,
- timeout/bütçe değerleri geçerli mi,
- disabled agent route'a girmiyor mu.

### 7.2 Agent contract

```csharp
public interface IAgentExecutor
{
    Task<AgentFinding> ExecuteAsync(
        AgentContext context,
        CancellationToken cancellationToken);
}

public sealed record AgentContext(
    Incident Incident,
    IReadOnlyList<Evidence> ExistingEvidence,
    AgentDefinition Definition,
    PromptRelease PromptRelease,
    ModelProfile ModelProfile,
    DateTimeOffset Deadline,
    string CorrelationId,
    string Tenant,
    string Environment);
```

### 7.3 Finding contract

```csharp
public sealed record Observation(
    string Statement,
    IReadOnlyList<EvidenceId> EvidenceIds,
    bool IsVerified);

public sealed record Hypothesis(
    string Statement,
    IReadOnlyList<EvidenceId> SupportingEvidenceIds,
    IReadOnlyList<EvidenceId> ContradictingEvidenceIds,
    decimal Confidence);

public sealed record AgentFinding(
    string Summary,
    IReadOnlyList<Observation> Observations,
    IReadOnlyList<Hypothesis> Hypotheses,
    IReadOnlyList<string> EvidenceGaps,
    IReadOnlyList<string> RecommendedNextChecks,
    decimal Confidence);
```

Chain-of-thought saklanmaz ve kullanıcıya açılmaz. Kullanıcıya özet,
kanıt referansları, hipotezler, belirsizlikler ve öneriler sunulur.

---

## 8. Prompt, model ve release yönetimi

### 8.1 Prompt artifact

Prompt'lar:

- C# koduna gömülmez.
- Git ile versionlanır.
- YAML veya JSON schema ile doğrulanır.
- Release manifest ile pinlenir.
- SHA-256 hash ile audit edilir.
- `latest` production workflow içinde kullanılmaz.

```text
prompts/
├── shared/
│   ├── system-policy.yaml
│   ├── evidence-policy.yaml
│   └── security-policy.yaml
├── classifier/
├── database-expert/
├── app-expert/
├── kubernetes-expert/
└── releases/
    └── sre-prompts-2026.08.24.1.yaml
```

Renderer için serbest şablon motoru yerine izin verilen değişkenleri
tanımlayan kontrollü bir renderer kullanılmalıdır:

```csharp
public interface IPromptRenderer
{
    RenderedPrompt Render(
        PromptDefinition definition,
        IReadOnlyDictionary<string, object?> variables);
}
```

Renderer:

- eksik required variable'da fail-fast,
- maksimum boyut guard,
- sensitive field redaction,
- SHA-256 hash,
- raw rendered prompt'u default olarak loglamama

kurallarını uygular.

### 8.2 Model assignment release

Model kimliği artık global bir `ModelProfile` değil, tenant sahipliğinde
immutable bir `ModelAssignmentRelease`'tir. Runtime çağrı anında profil
ismine göre çözümleme yapmaz; incident açılırken aktif release'in içindeki
her slot (`classifier`, `correlator` ve her serbest bırakılmış agent) için
tam `ModelDeploymentVersion` + `ProviderConnectionRevision` koordinatı
`ConfigurationSnapshot.ModelAssignment` içine pin edilir.

```text
ProviderConnection
  └── ProviderConnectionRevision  (immutable, hash + declared capabilities)
        └── ModelDeploymentVersion  (immutable, logical_name + version, temperature,
              timeout, cost/1M, required capabilities)
              └── ModelAssignmentRelease  (immutable, tenant + name + version)
                    ├── Classifier  → deployment version id
                    ├── Correlator  → deployment version id
                    └── Agents[name] → deployment version id
```

Her satır bir kez yazılır; PostgreSQL üzerinde `BEFORE UPDATE OR DELETE`
trigger'ı `MODEL_CONFIGURATION_INVALID` ile reddeder (D35). Yalnızca
`tenant_active_model_assignments` pointer'ı bir release'ten başka bir release'e
geçmek için güncellenebilir.

Her investigation başlangıcında alınan snapshot yalnızca release id'sini
değil pin edilmiş adayları da taşır:

```json
{
  "agent_release": "agents-release",
  "prompt_release": "sre-prompts-release",
  "tool_release": "tools-release",
  "policy_release": "policies-release",
  "model_assignment": {
    "release_id": "018f4d1b-1e56-7a11-9f2b-1c2a3b4c5d6e",
    "name": "bootstrap",
    "version": 1,
    "configuration_hash": "sha256:...",
    "classifier":  { "deployment_version_id": "...", "deployment_configuration_hash": "sha256:..." },
    "correlator":  { "deployment_version_id": "...", "deployment_configuration_hash": "sha256:..." },
    "agents":     { "database-expert": { "deployment_version_id": "...", "deployment_configuration_hash": "sha256:..." } }
  }
}
```

`LlmGateway` bu snapshot içindeki `PinnedModelCandidate`'i kullanır ve
`llm_calls` satırına hem istenen hem de gözlenen `provider` + `model` +
`assignment_release_id` + `deployment_version_id` + `provider_revision_id` +
üç yapılandırma hash'ini yazar; span üzerinde `sre.model.assignment_release`
ve `sre.model.deployment_version` etiketlenir (§16.1). Böylece hangi
release'in konfigüre ettiği ve hangi deployment version'ın cevapladığı bir
ay sonra bile tam olarak yeniden çözümlenebilir.

Shipped `config/models/*.yaml` artifact'ları runtime çözümleme yolu değildir:
ilk `ModelAssignmentRelease`'i seed etmek için `ModelAssignmentBootstrapper`
tarafından okunurlar (D34); ondan sonra kaynak yalnızca veritabanıdır.

---

## 9. Persistence ve veri modeli

### 9.1 Teknoloji

- PostgreSQL
- EF Core 10
- Npgsql provider
- EF Core migrations
- JSONB: labels, structured payload ve versioned report için kontrollü
- Büyük raw payload: object storage veya source pointer
- Transactional outbox
- Inbox/processed-events tablosu

### 9.2 Business tabloları

```text
incidents
incident_events
configuration_snapshots
evidence
agent_runs
findings
root_cause_reports
recommendations
approval_requests
remediation_actions
tool_calls
llm_calls
outbox_messages
inbox_messages
idempotency_keys
```

Workflow motorunun tabloları business tablolarından ayrı tutulur.

### 9.3 Idempotency

Database seviyesinde enforce edilir:

```text
UNIQUE(source, fingerprint, active_incident_window)
UNIQUE(event_id)
UNIQUE(idempotency_key)
UNIQUE(incident_id, report_version)
```

Alert ingestion:

1. source + normalized payload ile fingerprint üretir.
2. aynı event id geldiyse inbox üzerinden tekrar uygulamaz.
3. aktif incident varsa event timeline'a ekler.
4. resolved incident cooldown dışındaysa yeni incident açar.
5. business transaction içinde outbox mesajı yazar.
6. API `202 Accepted` döner.

### 9.4 EF Core transaction sınırı

Incident state değişimi, timeline event ve outbox kaydı aynı transaction'da
olmalıdır. Kafka publish transaction sonrasında outbox dispatcher tarafından
yapılır. Kafka publish başarısızlığı business transaction'ı geri almaz;
dispatcher bounded retry ve DLQ kullanır.

---

## 10. Alert ingestion ve messaging

### 10.1 Canonical alert contract

```csharp
public sealed record CanonicalAlert(
    string Source,
    string ExternalId,
    string Fingerprint,
    string Title,
    string? Description,
    string Service,
    string Environment,
    IncidentSeverity Severity,
    DateTimeOffset? StartedAt,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyDictionary<string, string> Annotations,
    string? RawPayloadReference);
```

Adapter'lar:

```text
AlertmanagerAdapter
DatadogAdapter
GrafanaAdapter
WebhookAdapter
KafkaAlertAdapter
```

Hepsi önce `CanonicalAlert` üretir; incident use case'i provider payload
formatını bilmez.

### 10.2 Kafka topic'leri

```text
sre.alert.received.v1
sre.incident.created.v1
sre.incident.investigation.requested.v1
sre.incident.updated.v1
sre.incident.completed.v1
sre.remediation.requested.v1
sre.remediation.completed.v1
sre.deadletter.v1
```

Envelope:

```csharp
public sealed record EventEnvelope<T>(
    Guid EventId,
    string EventType,
    int EventVersion,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    Guid? CausationId,
    T Payload);
```

Delivery semantiği at-least-once kabul edilir. Her consumer idempotent
olmalıdır. Offset yalnızca güvenli state ve inbox yazımı tamamlandıktan sonra
commit edilir.

---

## 11. Investigation workflow

### 11.1 Activity/node sınırları

```text
LoadIncident
→ ResolveConfigurationSnapshot
→ ClassifyIncident
→ SelectAgents
→ RunSelectedAgentsInParallel
→ CorrelateFindings
→ BuildRecommendations
→ DecideApproval
→ WaitForApproval (yalnızca gerekiyorsa)
→ ExecuteApprovedRemediation (V2)
→ PersistReport
→ Complete
```

Her activity:

- tek sorumluluk,
- açık retry sınıfı,
- açık timeout,
- cancellation-aware,
- yan etkiler için idempotent,
- telemetry span sahibi

olmalıdır.

### 11.2 Routing

Önce deterministic routing:

```text
component=database      → Database Expert
Kubernetes labels       → Kubernetes Expert
latency + HTTP 5xx      → App Expert
Kafka lag               → Queue Expert
Redis error             → Cache Expert
```

LLM classifier yalnızca deterministic sinyal yetersizse devreye girer.
Bir investigation'da varsayılan olarak 2–5 uzman seçilir. Disabled agent,
eksik tool ve düşük confidence durumları route aşamasında ele alınır.

### 11.3 Timeout hiyerarşisi

```text
HTTP client timeout
    < tool timeout
    < agent timeout
    < workflow deadline
```

Budget alanları:

```text
max_llm_calls
max_tool_calls
max_total_tokens
max_runtime_seconds
max_estimated_cost
```

Limit aşımında workflow kontrollü `PartialResult` veya `BudgetExceeded`
sonucu üretir; sonsuz LLM/tool döngüsü yoktur.

---

## 12. ASP.NET Core API

### 12.1 Endpoint'ler

```text
POST /v1/incidents
GET  /v1/incidents?status=&severity=&service=&environment=&source=&detected_from=&detected_to=&cursor=&limit=
GET  /v1/incidents/{incidentId}
GET  /v1/incidents/{incidentId}/timeline
GET  /v1/incidents/{incidentId}/evidence
GET  /v1/incidents/{incidentId}/report
POST /v1/incidents/{incidentId}/investigate
POST /v1/incidents/{incidentId}/retry
POST /v1/incidents/{incidentId}/cancel

GET  /v1/approvals
GET  /v1/approvals/{approvalId}
POST /v1/approvals/{approvalId}/approve
POST /v1/approvals/{approvalId}/reject
POST /v1/approvals/{approvalId}/edit

GET|PUT|DELETE /v1/budget
GET|POST       /v1/notification-channels
DELETE         /v1/notification-channels/{channelId}
POST           /v1/notification-channels/{channelId}/test

GET /health/live
GET /health/ready
GET /metrics
GET /v1/incidents/{incidentId}/events
```

Minimal API kullanılabilir; endpoint'ler application command/query handler'larına
delege edilmelidir. Endpoint içinde business rule yazılmaz.

```csharp
app.MapPost("/v1/incidents",
    async (
        CreateIncidentRequest request,
        CreateIncidentHandler handler,
        CancellationToken cancellationToken) =>
    {
        var result = await handler.HandleAsync(request, cancellationToken);
        return result.ToHttpResult();
    });
```

API kuralları:

- `ProblemDetails` ve stable error code,
- request body ve response size limitleri,
- request id/correlation id,
- authentication + RBAC,
- approval endpoint'lerinde güçlü kimlik doğrulama,
- rate limit,
- raw prompt/chain-of-thought response'a koymama,
- async işlem için `202 Accepted` + incident id.

SSE, workflow event'lerini yayınlar; raw LLM reasoning yayınlamaz:

```text
incident.status
agent.started
agent.completed
tool.started
tool.completed
correlation.completed
approval.required
incident.completed
```

---

## 13. Configuration ve composition root

`Program.cs` yalnızca composition root olarak kalmalıdır:

```csharp
builder.Services
    .AddOptions<DatabaseOptions>()
    .BindConfiguration("Database")
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddWorkflow(builder.Configuration);
builder.Services.AddOpenTelemetry(builder.Configuration);
```

Ayarlar:

```text
DatabaseOptions
KafkaOptions
LlmOptions
OpenAiOptions
PrometheusOptions
TelemetryOptions
SecurityOptions
WorkflowOptions
AgentRuntimeOptions
```

Kurallar:

- Secret ile normal config ayrılır.
- Production secret'ları Kubernetes Secret reference/Vault/managed secret
  store üzerinden gelir.
- `.env` yalnızca local development içindir.
- Required config startup'ta fail-fast doğrulanır.
- Secret değerleri `IOptions` log, exception veya prompt içine düşmez.

---

## 14. Resilience

HTTP çağrıları için `HttpClientFactory` ve resilience pipeline kullanılmalıdır.
Retry, timeout, circuit breaker ve concurrency limitleri her dependency için
ayrı tanımlanır:

```text
LLM provider
Prometheus
log backend
trace backend
Kubernetes API
Kafka
```

İlkeler:

- 429, timeout ve seçilmiş 5xx retry edilebilir.
- Invalid credential, policy denial ve deterministic validation retry edilmez.
- Exponential backoff + jitter.
- External write operation retry edilecekse idempotency key zorunlu.
- Provider/resource başına bulkhead.
- Kafka lag ve in-flight investigation backpressure sinyalidir.
- LLM outage sonsuz retry veya pod restart döngüsü oluşturmaz.

---

## 15. Security tasarımı

### 15.1 Kimlik ve yetki

ASP.NET Core JWT bearer/OIDC middleware kullanılır; local password/JWT
authentication yazılmaz. Tenant, environment, service allowlist ve risk
ceiling her tool execution context'inde taşınır.

Önerilen roller:

```text
SreViewer
SreInvestigator
SreApprover
SreOperator
SrePlatformAdmin
```

### 15.2 Tool güvenliği

- Read-only ve write registry'leri fiziksel olarak ayrılır.
- V1 yalnızca read-only tool içerir.
- Arbitrary shell/kubectl/SQL çalıştırılmaz.
- SQL yerine allowlisted typed query kullanılır.
- Kubernetes service account namespace-scoped ve read-only'dir.
- Write service account ayrı deployment ve ayrı policy kullanır.
- LLM credential görmez.
- Tool result prompt'a girmeden redact ve size-cap işleminden geçer.
- Tool output güvenilmeyen veri olarak işaretlenir.

### 15.3 Redaction

Pipeline:

```text
raw source
→ detector
→ redact/tokenize
→ evidence
→ compact prompt context
```

En azından password, API key, JWT, bearer token, cookie, connection string,
payment data ve kurumsal PII kategorileri işlenir. Redaction status evidence
metadata'sına yazılır.

### 15.4 Prompt injection

Retrieved log, trace, runbook veya ticket içeriği talimat değil veridir.
Sistem prompt'unda untrusted delimiter kullanılır; fakat güvenlik yalnızca
prompt'a bırakılmaz. Tool allowlist, authorization, policy engine ve typed
argument validation deterministic savunmalardır.

---

## 16. Observability

### 16.1 OpenTelemetry

Root span:

```text
incident.investigation
```

Child span'ler:

```text
classify
route
agent.execute
prompt.render
llm.call
tool.execute
correlate
approval.wait
remediation.execute
```

Span attribute olarak yalnızca metadata tutulur:

```text
incident.id
agent.name
agent.version
prompt.name
prompt.version
model.provider
model.name
tool.name
tool.risk
result.status
```

Prompt, token, credential veya raw tool result span attribute yapılmaz.

### 16.2 Temel metrikler

```text
sre_incident_started_total
sre_incident_completed_total
sre_incident_failed_total
sre_incident_duration_seconds
sre_workflow_resume_total
sre_workflow_timeout_total
sre_agent_run_total
sre_agent_run_duration_seconds
sre_llm_requests_total
sre_llm_input_tokens_total
sre_llm_output_tokens_total
sre_llm_estimated_cost_total
sre_tool_requests_total
sre_tool_policy_denied_total
sre_tool_duration_seconds
sre_prompt_render_failure_total
sre_kafka_consumer_lag
```

Structured JSON logging için `ILogger` abstraction'ı kullanılır. Log alanları
en azından timestamp, service, environment, trace id, span id, correlation
id, incident id, agent name, event name ve error code içermelidir.

---

## 17. Error taxonomy

Application'a provider exception sızdırılmaz. Dışarıya stable `ErrorCode`,
`Retryable` ve güvenli mesaj döner:

```text
CONFIG_INVALID
PROMPT_NOT_FOUND
PROMPT_RENDER_FAILED
MODEL_PROFILE_NOT_FOUND
MODEL_CAPABILITY_MISMATCH
LLM_RATE_LIMITED
LLM_TIMEOUT
LLM_PROVIDER_ERROR
LLM_STRUCTURED_OUTPUT_INVALID
TOOL_NOT_FOUND
TOOL_POLICY_DENIED
TOOL_TIMEOUT
TOOL_RATE_LIMITED
TOOL_EXECUTION_FAILED
INCIDENT_NOT_FOUND
INCIDENT_INVALID_STATE
WORKFLOW_DEADLINE_EXCEEDED
WORKFLOW_CANCELLED
APPROVAL_REJECTED
REMEDIATION_FAILED
```

ASP.NET Core exception middleware:

- correlation id üretir/korur,
- internal detail'i loglar,
- güvenli message döner,
- error code döner,
- validation error'larını 400,
- authorization'ı 403,
- not found'u 404,
- conflict/state violation'ı 409,
- transient dependency failure'ı policy'ye göre 503 olarak map eder.

---

## 18. Test stratejisi

### Unit

```text
Domain:
  - status transitions
  - fingerprint
  - risk policy
  - confidence bounds
  - evidence references

Application:
  - router
  - prompt release resolver
  - agent runtime
  - tool policy
  - budget
  - retry classification
```

### Contract

Her port aynı behavioral test suite ile test edilir:

```text
FakeLlmGateway
OpenAiLlmGateway
AnthropicLlmGateway
FakeMetricsProvider
PrometheusMetricsProvider
```

### Integration

Testcontainers ile:

- PostgreSQL,
- Kafka/Redpanda,
- Redis gerekiyorsa,
- mock observability backends

test edilir.

### Workflow

En az şu senaryolar otomasyona alınır:

1. DB-only alert.
2. App + DB parallel investigation.
3. Bir agent fail, diğerleri success.
4. Tool timeout.
5. Invalid structured output.
6. Worker restart sonrası resume.
7. Approval pause/resume.
8. Cancellation.
9. Workflow deadline.
10. Duplicate alert.
11. Outbox dispatcher retry.
12. Write tool approval olmadan denial.

### AI eval

Golden dataset her vaka için şunları taşır:

```text
incident payload
available evidence
expected relevant agents
expected evidence
expected RCA category
forbidden claims
expected tool families
```

Release gate metrikleri:

- evidence citation accuracy,
- unsupported claim rate,
- routing precision/recall,
- RCA top-1/top-k,
- schema validation rate,
- token ve maliyet regresyonu,
- latency regresyonu,
- prompt injection güvenlik skoru.

---

## 19. Deployment modeli

### 19.1 Container command'leri

```text
api
alert-worker
investigation-worker
```

Aynı application/infrastructure paketleri kullanılır; yalnızca host
composition'ı değişir. API latency, LLM-heavy worker yükünden izole edilir.

### 19.2 Kubernetes kaynakları

```text
Deployment: sre-ai-api
Deployment: sre-ai-alert-worker
Deployment: sre-ai-investigation-worker
Service
ConfigMap
Secret references
ServiceAccount
Role / RoleBinding
HPA
PodDisruptionBudget
NetworkPolicy
```

Investigation worker HPA'sı CPU yerine veya CPU'ya ek olarak Kafka lag,
queue depth ve in-flight investigation metriklerini kullanır.

Graceful shutdown:

1. Yeni mesaj kabulünü durdur.
2. In-flight activity'leri policy'ye göre tamamla/iptal et.
3. Güvenli state'i checkpoint et.
4. Offset'i yalnızca güvenli noktada commit et.
5. DB, Kafka ve HTTP client'larını kapat.

### 19.3 Local development

```text
PostgreSQL
Kafka/Redpanda
Redis (opsiyonel)
OpenTelemetry Collector
Mock Prometheus/log backend
API
Alert Worker
Investigation Worker
Workflow engine
```

Local ortam production observability endpoint'lerine varsayılan olarak
bağlanmaz. Provider yerine fake/mock adapter kullanılabilir.

---

## 20. V1 kapsamı

### V1

- ASP.NET Core API
- webhook + Kafka alert ingestion
- PostgreSQL + EF Core
- transactional outbox/inbox
- durable investigation workflow
- App, Database ve Kubernetes read-only experts
- Prometheus, logs ve Kubernetes read-only tools
- LLM gateway + tek provider adapter
- prompt/model/agent/tool registry
- immutable configuration snapshot
- evidence id ve RCA report
- structured output validation
- OpenTelemetry
- API incident result
- remediation execution yok

### V1.1

- trace provider,
- database observability,
- deployment metadata,
- SSE progress,
- daha güçlü deterministic routing,
- Redis cache,
- dashboard/read model.

### V2

- human approval,
- low/medium risk remediation,
- provider fallback,
- vector/RAG,
- automated eval release gate.

### V3

- kontrollü auto-remediation,
- topology-aware reasoning,
- incident outcome feedback,
- tenant başına policy isolation.

---

## 21. Uygulama sırası

```text
1. .NET 10 solution + repository quality
2. Domain entities/value objects/policies
3. Application contracts and ports
4. Options validation + composition root
5. Prompt/model/agent/tool registry schemas
6. LLM gateway + fake provider
7. Tool gateway + policy engine
8. Prometheus fake adapter
9. EF Core persistence + outbox/inbox
10. First vertical slice
11. App/DB/Kubernetes agent runtime
12. Durable workflow adapter
13. Parallel fan-out/fan-in + resume
14. ASP.NET Core API + SSE
15. Kafka ingestion and worker separation
16. OpenTelemetry + dashboards
17. Security and redaction hardening
18. Integration/workflow/security tests
19. Golden evals and release gates
20. Docker/Helm/Kubernetes production readiness
21. HITL and remediation
```

### İlk vertical slice

```text
POST /v1/incidents
→ Incident aggregate
→ configuration snapshot
→ durable workflow
→ Database Expert
→ fake Prometheus tool
→ structured AgentFinding
→ Correlator
→ PostgreSQL RCA report
→ GET /v1/incidents/{id}/report
```

Bu slice içinde bile production sınırları korunur:

- LLM doğrudan tool çağırmaz.
- Prompt release pinlidir.
- Tool Gateway policy uygular.
- Evidence ID zorunludur.
- Business state PostgreSQL'de tutulur.
- Workflow state restart sonrası devam edebilir.

---

## 22. .NET 10 kalite baseline'ı

`Directory.Build.props` için önerilen ilkeler:

```xml
<TargetFramework>net10.0</TargetFramework>
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<AnalysisMode>Recommended</AnalysisMode>
<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
```

CI minimumları:

```text
dotnet restore --locked-mode
dotnet format --verify-no-changes
dotnet build --no-restore -warnaserror
dotnet test --no-build
dotnet test --filter Category=Integration
dotnet list package --vulnerable
secret scan
container scan
prompt schema validation
AI eval smoke
```

Central package management ile tüm NuGet sürümleri
`Directory.Packages.props` içinde pinlenir. Uygulama ve worker projeleri
aynı dependency graph'ı paylaşsa da her proje yalnızca kullandığı paketi
declare eder.

---

## 23. Son karar özeti

```text
Domain/Application       → saf C# ve interface'ler
API                      → ASP.NET Core Minimal APIs
Workers                  → Generic Host + BackgroundService
Persistence              → EF Core 10 + PostgreSQL
Messaging                → Kafka adapter + outbox/inbox
Durable workflow         → Temporal .NET adapter
LLM abstraction          → ILlmGateway + Microsoft.Extensions.AI adapter
Prompt management        → immutable YAML/JSON releases
Tool security            → typed Tool Gateway + Policy Engine
Observability            → OpenTelemetry
Testing                  → xUnit + Testcontainers + contract/eval suites
Deployment               → Docker + Helm + Kubernetes
```

En kritik prensipler değişmez:

1. Evidence first.
2. Read-only ve write capability kesin ayrıdır.
3. Tool erişimi yalnızca deterministic gateway üzerinden yapılır.
4. Prompt/model/agent/tool/policy release'leri workflow başlangıcında pinlenir.
5. Business state ile workflow state ayrı tutulur.
6. At-least-once messaging idempotent consumer'larla ele alınır.
7. Structured output ve security policy prompt'a bırakılamaz.
8. V1 dar, read-only ve ölçülebilir bir vertical slice olarak çıkar.

---

## 24. Agent Control Plane ve Feature Flag Mimarisi

Agent'ların kullanılabilirliği feature flag'lerle yönetilir; ancak bu yapı
yalnızca `enabled: true/false` şeklinde basit bir açma-kapama mekanizması
olmamalıdır. Agent Registry, flag değerlendirme servisi ve rollout policy
birlikte bir **Agent Control Plane** oluşturur.

### 24.1 Agent availability state

```csharp
public sealed record AgentAvailability(
    string AgentName,
    string Version,
    AgentAvailabilityState State,
    string? Reason,
    DateTimeOffset EvaluatedAt);

public enum AgentAvailabilityState
{
    Disabled,
    Shadow,
    Canary,
    ReadOnly,
    Active,
    KillSwitched
}
```

State'lerin anlamı:

```text
Disabled      → Agent hiçbir workflow'a seçilmez.
Shadow        → Çalışır; sonucu RCA veya kullanıcı kararını etkilemez.
Canary        → Sınırlı tenant/environment/yüzde için aktiftir.
ReadOnly      → Yalnızca güvenli read-only tool'ları kullanabilir.
Active        → Normal routing ve execution'a açıktır.
KillSwitched  → Acil güvenlik, kalite veya maliyet nedeniyle durdurulmuştur.
```

### 24.2 Agent flag tanımı

```yaml
name: database-expert
version: v3

availability:
  state: active
  environments:
    production: true
    staging: true

routing:
  allowed_incident_types:
    - database
    - timeout

execution:
  mode: read_only

rollout:
  mode: tenant_allowlist
  tenants:
    - tenant-a
    - tenant-b
```

Flag kapsamları şu seviyelerde desteklenebilir:

```text
global
tenant
environment
service
incident type
agent
agent version
prompt release
model profile
tool family
```

### 24.3 Router akışı

Router yalnızca `AgentFlagEvaluator` tarafından kullanılabilir görülen
agent'ları seçer:

```text
Incident
  → deterministic classification
  → Agent Registry
  → availability/rollout evaluation
  → capability ve tool validation
  → agent selection
```

Örneğin Database Expert disabled ise:

1. Agent routing'den filtrelenir.
2. Filtrelenme sebebi audit event olarak yazılır.
3. Uygun fallback agent değerlendirilir.
4. Uygun agent kalmadıysa kontrollü
   `INSUFFICIENT_AGENT_CAPABILITY` sonucu üretilir.

```csharp
public interface IAgentFlagEvaluator
{
    Task<AgentAvailability> EvaluateAsync(
        AgentDefinition agent,
        IncidentContext incident,
        ConfigurationSnapshot snapshot,
        CancellationToken cancellationToken);
}
```

### 24.4 Flag ile policy ayrımı

Feature flag, agent'ın çalışıp çalışamayacağını belirler. Güvenlik ve yetki
kararını ise Policy Engine verir:

```text
Agent Flag
  + Agent Capability
  + Tool Allowlist
  + Tenant Policy
  + Environment Policy
  + Approval Policy
  → nihai execution kararı
```

`enabled: true`, agent'ın write veya destructive işlem yapabileceği anlamına
gelmez. Flag sistemi authorization veya remediation güvenlik kontrolünün
yerine geçemez.

### 24.5 Configuration snapshot

Workflow başladığında agent availability ve rollout kararları snapshot'a
alınır:

```json
{
  "agent_release": "agents-release",
  "flag_snapshot": {
    "database-expert": "active",
    "kubernetes-expert": "disabled",
    "new-correlator": "shadow"
  },
  "policy_release": "policies-v4"
}
```

Devam eden incident, yeni flag değişikliğinden etkilenmez; böylece replay ve
audit davranışı deterministik kalır. Güvenlik veya maliyet acil durumu olan
`KillSwitched` flag'i bu kuralın kontrollü istisnasıdır ve aktif workflow'u da
durdurabilir.

### 24.6 Shadow ve canary rollout

Yeni prompt/model/agent release'leri önce shadow modda çalıştırılabilir:

```text
production incident
  → mevcut agent sonucu kullanıcıya gider
  → shadow agent aynı evidence ile çalışır
  → sonuç yalnızca eval/karşılaştırma sistemine gider
```

Canary için örnek:

```yaml
rollout:
  mode: percentage
  value: 10
```

Alternatif olarak tenant, environment, service veya incident type allowlist'i
kullanılabilir. Kalite, maliyet veya güvenlik metriği eşik dışına çıkarsa
rollout otomatik olarak önceki availability state'ine alınabilir.

### 24.7 Flag audit trail

Her flag değişikliği append-only audit event olarak tutulur:

```text
agent_name
agent_version
old_state
new_state
scope
changed_by
reason
effective_at
flag_release
```

Örnek event:

```text
AGENT_DISABLED
agent: database-expert
reason: elevated unsupported-claim rate
```

### 24.8 Flag persistence ve cache

Flag kaynağı versioned YAML/JSON release, PostgreSQL control-plane tablosu
veya merkezi configuration provider olabilir. Production'da `latest`
doğrudan okunmaz; immutable flag release resolve edilir.

Cache kullanılacaksa:

- release ve scope cache key'e dahil edilir,
- kısa TTL ve explicit invalidation bulunur,
- approval state veya remediation sonucu flag cache'lenmez,
- kill switch cache'den daha öncelikli değerlendirilir.

Önerilen gelecekteki tablolar:

```text
agent_definitions
agent_flag_releases
agent_flag_rules
agent_flag_audit_events
```

### 24.9 V1 ve sonraki sürümler

```text
V1:
  enabled/disabled
  environment scope
  read_only mode
  kill switch
  immutable workflow snapshot

V1.1:
  tenant allowlist
  shadow mode
  agent version pinning

V2:
  canary percentage rollout
  automatic rollback
  prompt/model release ile birlikte flag promotion
  dashboard ve anomaly-based kill switch
```

Bu tasarımla yeni bir agent eklemek:

```text
agent definition
+ prompt release
+ model profile
+ tool allowlist
+ feature flag
+ eval cases
→ kontrollü rollout
```

haline gelir; doğrudan kod deploy ederek agent'ı production'a açmak gerekmez.
