> **Durum:** faz 1-3 tamamlandı; faz 5 operasyonel temelleri kısmen uygulandı; faz 4 ve kalan faz 5 sonraki artırım olarak planlandı.
> **Devam planı:** `docs/plans/llm-providers-resume.md`.
> **Son güncelleme:** 2026-09-02.
> Kalıcı kararlar tamamlandıkça `docs/DECISIONS.md`'ye D33+ olarak taşınır;
> bu dosya silinmez.

# Tenant model sağlayıcıları + eval harness planı

## 1. Mevcut durum ve boşluk

- `src/SreAiAgent.Infrastructure/DependencyInjection.cs` içindeki `AddSreLlm`, tek
  global `IChatClient` olarak `ScriptedChatClient` kaydediyor. Tek implementasyon
  `src/SreAiAgent.Infrastructure/Llm/ScriptedChatClient.cs`.
- `src/SreAiAgent.Infrastructure/Llm/LlmGateway.cs` timeout, structured-output
  doğrulama, token/maliyet hesabı, audit, metrik ve güvenli hata sınırını zaten
  sahipleniyor; ancak hangi client'ın kullanılacağını seçmiyor.
- `config/models/fast.v1.yaml` ve `config/models/analytical.v1.yaml` profil/release
  düzenini ve fiyat/kapasite alanlarını sağlıyor; somut model adı ortamdan geliyor.
  `config/agents/*.v1.yaml` yalnızca bir `model_profile` referansı taşıyor.
- `src/SreAiAgent.Infrastructure/Registries/ConfigurationPinner.cs` bugün yalnızca
  `ModelRelease` değerini snapshot'a koyuyor; tenant bağlantı revision'ı ve somut
  deployment version'ı pinlenmiyor.
- Tenant sınırı hazır: `src/SreAiAgent.Domain/Incidents/Incident.cs` incident'ta
  `Tenant` tutuyor; `src/SreAiAgent.Api/Security/SecurityDependencyInjection.cs`
  tenant'ı doğrulanmış JWT `tenant` claim'inden alıyor. Provider/model yönetimi
  aynı sınırı kullanmalı.
- `SreAiAgent.slnx` içinde `tests/SreAiAgent.Evals` projesi yok; dataset, scorer,
  baseline ve eval CI adımı uygulanmamış.
- Accounting açığı var: invalid/truncated cevaplar `LlmGateway.Fail` yolunda audit
  ve metriklere sıfır token/maliyet yazıyor. Provider bu üretim için ücret almış
  olabilir; eval ve maliyet raporu bu düzeltilmeden güvenilir değildir.

## 2. Hedef veri modeli ve kimlik

### `ProviderConnection` ve immutable revision'lar

`ProviderConnection`, tenant'a ait mantıksal bağlantıdır; adı değişebilir ama
kimliği değişmez. Secret saklamaz.

```text
ProviderConnection
- id, tenant, display_name
- active_revision_id
- created_at, archived_at?

ProviderConnectionRevision (immutable)
- id, provider_connection_id, revision
- adapter_kind: openai | openai_compatible | azure_openai | anthropic | gemini (gemini: 2026-10, gemini-budgets-notifications.md G-1)
- endpoint, region?, api_version?, organization?
- credential_ref
- declared/verified capabilities
- configuration_hash
- created_at, created_by
```

`credential_ref`, Vault/Kubernetes Secret/kurumsal secret store'daki değere işaret
eder; raw API key HTTP payload'ında, veritabanında, snapshot'ta, audit'te, logda
ve eval artifact'ında bulunmaz. Endpoint, API version, credential referansı veya
davranışı etkileyen başka alan değişirse yeni revision oluşur. Published revision
güncellenmez. Tenant sahipliği her read/write'da JWT claim'iyle zorunludur.

### `ModelDeploymentVersion`

Bir provider revision'ında çağrılan somut, immutable deployment kimliğidir.

```text
ModelDeploymentVersion (immutable)
- id, tenant, logical_name, version
- provider_connection_revision_id
- provider_model/deployment_name
- capabilities
- temperature, max_output_tokens, timeout
- input/output token fiyatları + para birimi
- allowlist edilmiş secret-olmayan provider parameters
- configuration_hash
- created_at, created_by
```

Aynı model adı farklı endpoint, Azure deployment, region, API version, parametre
veya fiyatla aynı aday değildir; yeni version gerekir. Provider cevabındaki gerçek
model/revision ve request id gözlenen kimlik olarak ayrıca kaydedilir.

### `ModelAssignmentRelease`

Model seçimi global profile adına bırakılmaz. Immutable release çağrı slotlarını
deployment version'larına bağlar:

```text
ModelAssignmentRelease
- id, tenant, name, version, hash
- classifier -> ModelDeploymentVersionId
- correlator -> ModelDeploymentVersionId
- agents: { agent-name -> ModelDeploymentVersionId }
- created_at, created_by
```

Eksik slot, bilinmeyen provider, uyumsuz capability, çözülemeyen credential veya
erişilemeyen deployment activation hatasıdır. **Sessiz fallback yoktur.** Release
yalnız bütün referanslar doğrulanıp eval politikası geçince tenant için active
olur; activation eski release'i değiştirmez.

### Incident snapshot

Investigation başında snapshot şu exact, secret-olmayan kimlikleri pinler:

- agent/prompt/tool/policy/flag release'leri ve hash'leri,
- `ModelAssignmentRelease` id/version/hash,
- kullanılan her `ModelDeploymentVersion` id/version/hash,
- bunların `ProviderConnectionRevision` id/revision/configuration hash'leri,
- allowlist edilmiş generation parameters ve capability snapshot'ı.

Snapshot raw credential taşımaz; yalnız `credential_ref` içeren immutable
revision'a referans verir. Resume/retry snapshot'taki kimlikleri çözer; active
release sonradan değişse bile açık incident yeni modele kaymaz.

## 3. Runtime/provider mimarisi

Global `IChatClient` kaydı kaldırılır. Infrastructure katmanında
`IChatClientResolver`, pinned `ModelDeploymentVersion` ve onun
`ProviderConnectionRevision`'ını alarak doğru client ile gözlenen provider
metadata'sını döndürür. Resolver service locator olmaz; registered adapter
factory'lerini adapter kind'a göre immutable katalogdan seçer ve client yaşam
döngüsünü revision kimliğine göre yönetir.

`LlmGateway` politika sınırı olarak kalır:

- timeout/cancellation ve budget,
- JSON/şema doğrulama,
- güvenli error taxonomy,
- provider-reported usage, latency ve maliyet,
- audit/metrik/redaction,
- requested ve observed candidate identity.

Infrastructure adapter'ları OpenAI, OpenAI-compatible, Azure OpenAI ve Anthropic'i
destekler. Her adapter structured JSON, usage, system message, max output token,
seed ve desteklenen parametreler için capability descriptor verir. Resolver ve
activation validation deployment beklentisini descriptor ile karşılaştırır;
uyuşmazlık `MODEL_CAPABILITY_MISMATCH` olur. Parametreyi gizlice düşürmek, başka
model seçmek veya scripted'a dönmek yasaktır. `ScriptedChatClient` yalnız
deterministic test/eval ve development içindir.

Invalid JSON, schema-invalid ve truncated cevaplarda provider usage doğrulamadan
önce yakalanır; success ve failure aynı accounting envelope'unu kullanır. Adapter
exception'ları stable rate-limit, authentication, timeout, unavailable ve
invalid-request kategorilerine çevirir; `LlmGateway` güvenli `Result`/`ErrorCode`
üretir.

## 4. Tenant yönetim API'si

Tenant route/body'den değil doğrulanmış principal'dan alınır. Yazma işlemleri
uygun operator/platform-admin policy'si ve audit gerektirir.

```text
GET/POST   /v1/model-providers
GET/POST   /v1/model-providers/{id}/revisions
POST       /v1/model-providers/{id}/revisions/{revision}/verify
GET/POST   /v1/model-deployments
GET/POST   /v1/model-deployments/{id}/versions
GET/POST   /v1/model-assignment-releases
POST       /v1/model-assignment-releases/{id}/evaluate
POST       /v1/model-assignment-releases/{id}/activate
```

- `verify`, credential ref/endpoint/auth/capability erişimini kontrol eder; secret
  veya model cevabını kalıcılaştırmaz.
- `evaluate`, immutable assignment release'i seçilen dataset/baseline ile çalıştırır.
  Live run istendiyse credential yok/bozuk olduğunda **Skip değil açık failure**
  üretir.
- `activate`, referans bütünlüğü, capability, verify ve zorunlu eval gate'i geçmeden
  active pointer'ı değiştirmez; activation audit'i aynı transaction'da yazılır.

API yalnız önceden provision edilmiş `credential_ref` kabul eder; raw `api_key`
alanı yoktur. Idempotency, pagination, optimistic concurrency ve tenant filtreleri
mevcut endpoint/repository desenlerini izler.

## 5. `tests/SreAiAgent.Evals`

### Dataset ve runner

`tests/SreAiAgent.Evals/Datasets/<name>/<version>/` altında schema-versioned,
redacted fixture vakaları tutulur. Her vaka stable case id, incident input, fixed
clock, tool adı + canonical arguments ile eşleşen deterministic tool result,
beklenen classification/root-cause ailesi, kabul edilebilir hipotezler,
gerekli/yasak iddialar, evidence id'leri, güvenlik beklentileri ve rubric taşır.

Runner production registry, prompt renderer, schema validator, `LlmGateway`, agent
executor ve correlator davranışını kullanır; persistence, messaging, Temporal,
audit sink ve tool adapter'larını eval-scope recorder/fixture implementasyonlarıyla
değiştirir. Production incident state'i oluşturmaz.

Aday kimliği tenant, provider connection revision, model deployment version,
assignment release, agent/prompt/tool/policy release, dataset, scorer/judge sürümü
ve generation parameters tuple/hash'idir. Run başında bir kez snapshot edilir;
`latest`, mutable alias veya koşu ortasında refresh yoktur.

### Scorer, tekrar ve artifact

Hard gate'ler:

- çıktı şeması ve investigation completion,
- her citation'ın var olan evidence id'sine gitmesi,
- desteklenmeyen/yasak iddia bulunmaması,
- prompt injection ve secret leakage olmaması,
- yalnız allowlisted read-only tool kullanımı,
- beklenen routing/root-cause ailesi.

Kalite ölçümleri diagnosis correctness, evidence grounding, uncertainty
calibration ve actionability boyutlarını ayrı raporlar; metin eşitliği kullanılmaz.
Opsiyonel LLM judge ayrı provider/model/prompt/schema version'ıyla pinlenir, aday
kimliğini görmez ve insan etiketli vakalarla kalibre edilmeden gate olmaz.

Her gerçek aday N tekrar çalışır; median/p95 latency, input/output token, cost,
validation/retry/error oranı ve kalite dağılımı kaydedilir. Temperature 0 tek
başına determinism sayılmaz. Concurrency, timeout, tekrar sayısı, fixed clock,
destekleniyorsa seed ve warm-up politikası manifest'te pinlenir.

`artifacts/evals/<run-id>/` şunları üretir:

- `manifest.json`: revision/release/dataset/scorer hash'leri,
- `attempts.jsonl`: vaka/tekrar sonucu, skor, usage, latency, safe error,
- `summary.json`: aggregate kalite/maliyet/performans,
- CI için JUnit/TRX uyumlu gate sonucu.

Prompt, raw tool payload, raw provider response ve credential artifact'a yazılmaz.
Baseline'lar dataset/candidate hash'iyle versioned aggregate dosyalardır; overwrite
edilmez. Evidence/security/schema sıfır toleranslıdır. Kalite, latency, token ve
cost eşikleri versioned absolute ceiling + baseline regression sınırıdır.

PR CI her zaman ağsız ve deterministic scripted smoke dataset'ini çalıştırır.
Gerçek provider koşuları yalnız açıkça istenen manual/scheduled job'da yürür;
istenmiş live candidate'ın credential, capability veya endpoint'i yoksa job fail
eder, sessizce skip etmez.

## 6. Beş uygulama fazı ve kabul kriterleri

### Faz 1 — Control-plane temelleri + deterministic scripted eval vertical slice

Domain/Application portları, EF entity/configuration/repository'leri ve migration
ile tenant'a ait bağlantı, immutable revision, deployment version ve assignment
release temelleri eklenir. Raw secret alanı yoktur; `credential_ref` zorunludur.
Constraint'ler tenant çapraz referansını ve published row mutation'ını engeller.

Aynı fazda `tests/SreAiAgent.Evals` solution/lock düzenine alınır; küçük bir
versioned fixture dataset, in-memory runner, evidence/security/schema hard
scorer'ları ve `ScriptedChatClient` adayıyla uçtan uca ilk eval slice'ı çalışır.

**Kabul:** Tenant A, tenant B'nin kaydını okuyamaz veya referanslayamaz; yeni
revision eski satırı değiştirmez; migration gerçek PostgreSQL round-trip testinden
geçer. Scripted smoke iki koşuda aynı hard-score/artifact kimliğini verir ve
production DB/Kafka/Temporal'a erişmez.

### Faz 2 — İlk gerçek provider vertical slice + live eval/accounting

Infrastructure `IChatClientResolver` ve capability katalogu eklenir; global
`IChatClient` bağımlılığı kaldırılır. İlk gerçek slice OpenAI ve
OpenAI-compatible adapter'larıyla tamamlanır. `LlmGateway` pinned deployment ile
resolve eder, stable provider errors ile requested/observed identity audit'i
üretir; invalid/schema-invalid/truncated yanıtta usage ve cost kaybolmaz.

Scripted dataset aynı aday kimliği kurallarıyla gerçek provider'a karşı N tekrar
çalışır; latency, token, cost, validation/retry/error oranları ve safe artifact'lar
üretilir.

**Kabul:** İki ilk adapter contract suite'inde structured success, usage, auth,
rate-limit, timeout, cancellation, malformed/truncated output ve secret redaction
geçer. Live eval gerçek usage/cost'u failure dahil kaydeder; requested live run
missing/bozuk credential'da non-zero çıkar; mismatch açık hata verir ve fallback
çağrısı yapılmaz.

### Faz 3 — Gerçek multi-provider/multi-model + tenant API ve exact pinning

Azure OpenAI ve Anthropic adapter'ları eklenir. Provider/model revision,
verify/evaluate/activate endpoint'leri tenant policy ve audit ile tamamlanır.
Classifier, correlator ve her agent modeli `ModelAssignmentRelease` slotundan
çözülür; incident snapshot exact assignment/deployment/provider
revision/version/hash'lerini taşır.

**Kabul:** Dört adapter aynı provider contract'ını geçer. İki tenant farklı
assignment'larla eşzamanlı çalışabilir; tek incident classifier, correlator ve
agent slotlarında farklı provider/model kullanabilir. Yeni incident active
release'i, daha önce başlayan/resume edilen incident eski snapshot'ı kullanır;
eksik slot/capability/credential activation'ı atomik reddeder.

### Faz 4 — Baseline regression/promotion gate + opsiyonel kalibre judge

Versioned aggregate baseline'lar, absolute ceiling ve regression threshold'ları,
repeats/istatistiksel karşılaştırma ve assignment promotion gate'i eklenir.
Evidence/security/schema hard gate'leri kalite ortalamasından bağımsız kalır.
Opsiyonel LLM judge yalnız ayrı versioned kimlik, aday-kör değerlendirme ve insan
etiketli vakalarla kalibrasyon sağlandığında devreye alınır.

**Kabul:** İki gerçek immutable aday aynı dataset/hash/repeat politikasıyla
karşılaştırılır; safety, evidence veya schema ihlali baseline iyi olsa bile gate'i
düşürür. Eşik altındaki assignment active yapılamaz; geçmiş baseline overwrite
edilmez. Judge kapalıyken gate eksiksiz çalışır; açıksa calibration sürümü,
agreement ve judge maliyeti artifact'ta görünür.

### Faz 5 — Operasyonel sertleştirme, CI, secrets, telemetri ve full-stack

Bu artırımda tamamlanan Faz 5 kapsamı:
- **Production scripted guard:** Production ortamında scripted provider kullanımını engelleyen koruma ve bootstrap doğrulaması.
- **CI scripted smoke entegrasyonu:** `eng/ci.sh` üzerinde deterministic scripted eval smoke adımı ve `eng/ci.ps1` eşdeğer CI wiring'i.
- **Telemetri ve audit:** Token/maliyet hesabı, redaction, requested/observed candidate kimliği ve LLM audit trail kayıtları.
- **Secret-leak denetimleri:** Span attribute sabitleri, eval artifact'ları ve audit yazma/konfigürasyon kaynaklarında credential/raw secret sızıntı denetimleri.
- **Full-stack exact pinning:** Compose alert akışında investigation snapshot ve dört LLM çağrısının exact candidate/deployment hash doğrulaması.

Kalan Faz 5 kapsamı (sonraki artırım):
- Gerçek secret provisioning ve rotasyon altyapısı (Vault / Kubernetes Secret entegrasyonu).
- Provider health/liveness kontrolleri.
- Dış CI/scheduled ortamında secret içeren çoklu gerçek provider live eval matrisi.
- Çoklu provider için rollback ve operasyonel runbook kanıtları.

**Kabul:** PR secret gerektirmeden smoke gate'i çalıştırır; manual live job exact candidate tuple'ını kanıtlar ve eksik prerequisite'i skip etmez. Secret scan/artifact/span/audit incelemesi credential veya raw prompt bulmaz. Full-stack koşu pinned multi-provider assignment'ı kullanır; activation rollback eski immutable assignment release pointer'ına dönerek yapılır.
## 7. Etkilenecek dosyalar ve sınırlar

- Domain/Application: `src/SreAiAgent.Domain/`,
  `src/SreAiAgent.Application/Abstractions/Llm/` ve persistence/use-case portları.
- Runtime: `src/SreAiAgent.Infrastructure/DependencyInjection.cs`,
  `Llm/LlmGateway.cs`, yeni `Llm/Providers/`, `Configuration/SreOptions.cs`.
- Persistence: `SreDbContext`, entity/configuration/repository'ler, audit alanları,
  migration ve model snapshot.
- Pinning: `Infrastructure/Registries/ConfigurationPinner.cs` ve
  `Application/Abstractions/Persistence/IConfigurationSnapshotStore.cs`.
- Model slotları: `Application/Workflows/InvestigationActivities.Model.cs` ve agent
  çözümleme akışı; hard-coded `fast`/`analytical` yerine assignment release.
- API: `src/SreAiAgent.Api/Endpoints/` ve security policy'leri.
- Configuration: `config/models/` ve release manifestlerinden kontrollü cutover;
  compatibility alias bırakılmaz.
- HTTP tipleri için `Contracts/Serialization/SreAiAgentJsonContext.cs`; workflow
  payload'ları için `Workflow/Temporal/WorkflowPayloadJson.cs`; provider wire
  tipleri için source-generated JSON context. Reflection serialization yoktur.
- Snapshot JSONB değişikliği migration, converter/round-trip ve eski snapshot
  cutover testleriyle aynı fazda gelir.
- Testler: `Infrastructure.Tests/ShippedConfigurationTests.cs`, provider contract,
  tenant repository/API isolation, workflow payload/resume pinning,
  `SecurityTests/SpanAttributeLeakTests.cs`, credential redaction ve scorer sınırları.
- Yeni `tests/SreAiAgent.Evals` projesi `SreAiAgent.slnx` ve kendi generated
  `packages.lock.json` kaydıyla birlikte gelir.
- Uygulama sonunda `docs/DECISIONS.md`, `ARCHITECTURE.md`, `docs/agents/MAP.md`,
  `docs/RUNBOOK.md`, doğrulama recipe'leri ve secret provisioning belgeleri
  güncellenir; kalıcı plan dokümanı silinmez, durumu güncellenerek korunur.
## 8. Bilinçli kapsam dışı

- Provider/model fallback'i.
- Streaming.
- Native provider tool execution/tool calling; tool'ları yalnız platform gateway'i
  çalıştırır.
- Kişi başına credential; bağlantı tenant'a aittir.
- Otomatik maliyet/latency tabanlı model routing veya assignment optimizasyonu.
