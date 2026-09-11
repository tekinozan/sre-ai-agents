> **Durum:** ADP-003 uygulama ve test rehberi.
> **Kapsam:** Mevcut entegrasyon desteğinin kanıtlanması ve eksik doğrulamanın planlanması.
> **Kapsam dışı:** Bu belge kod, yeni provider, yeni test projesi veya release onayı oluşturmaz.

# ADP-003 — entegrasyon desteği ve test rehberi

## Amaç ve bitiş tanımı

ADP-003, bir entegrasyon için üç farklı hüküm kurar:

| Durum | Anlamı | Gerekli kanıt |
|---|---|---|
| Uygulandı | Kaynakta adapter veya endpoint bulunur. | Dosya ve davranış testi |
| Doğrulandı | İddia edilen davranış otomatik testle kanıtlanır. | Test adı, sonuç ve tarih |
| İlk sürümde desteklenir | Release gate ve işletim kanıtı tamamdır. | Redakte edilmiş release evidence |

Uygulandı veya doğrulandı, tek başına production desteği değildir. Kanıt yoksa
**bilinmiyor** yazılır. Model adı, provider pazarlama bilgisi veya tahmin kanıt
sayılmaz. Bitiş çıktısı güncel matris, izlenebilir test kanıtı ve release evidence
kaydıdır. ADP-003 sonraki kod işleri için backlog üretir; gereksiz provider eklemez.

## Bugünkü envanter

| Entegrasyon | Uygulandı | Doğrulandı | İlk sürüm desteği | Kanıt ve sınır |
|---|---|---|---|---|
| Scripted LLM | Evet | Evet | Development ve deterministic eval | `ScriptedProviderChatClient.cs`, CI scripted eval |
| OpenAI | Evet | Adapter unit | Bilinmiyor | `OpenAiProviderChatClient.cs`; live release kanıtı yok |
| OpenAI-compatible | Evet | Adapter unit | Bilinmiyor | `OpenAiCompatibleProviderChatClient.cs`; endpoint matrisi yok |
| Azure OpenAI | Evet | Adapter unit | Bilinmiyor | `AzureOpenAiProviderChatClient.cs`; live release kanıtı yok |
| Anthropic | Evet | Adapter unit | Bilinmiyor | `AnthropicProviderChatClient.cs`; live release kanıtı yok |
| Alert ingress | Evet | Unit ve integration katmanları | Bilinmiyor | `AlertEndpoints.cs`, alert ve tenant testleri |
| Prometheus ve Loki | Evet | Unit ve contract katmanları | Bilinmiyor | provider testleri ve Testcontainers contract testleri |

Gerçek HTTP adapterları `src/SreAiAgent.Infrastructure/Llm/Providers/` altındadır.
`Tests/SreAiAgent.Infrastructure.Tests/ProviderAdapterTests.cs`, OpenAI,
OpenAI-compatible, Azure OpenAI ve Anthropic için aynı `HttpAdapters()` verisiyle
çalışır. Bu adapter sözleşmesi başlangıç noktasıdır; seçili modelin her
capabilitysini canlıda kanıtlamaz.

Bu dört adapter desteklenecek model veya endpointlerin kapalı listesi değildir.
Provider/protocol adapter, model ID ve endpoint/deployment ayrı kayıtlardır: aynı
Qwen model ID yerel veya hosted deploymentta farklı capability gösterebilir; Kimi
veya Gemini için protokol varsayımı yapılmaz. Compatible endpoint abstraction'ı
runtime capability garantisi değildir. Yeni native protokol yalnız ürün ihtiyacıyla
ayrı adapter işi olur. User-managed registry arbitrary model ID, manual entry ve
isteğe bağlı discovery destekler; pinned revision referansları silinmez, archive
edilir.

Mevcut planın sınırı korunur: native provider tool calling ilk sürümde yoktur;
read-only tool çağrılarını platform gateway yürütür. Bu nedenle tool calling
etiketi eklenmez. Structured output için wire map yeterli değildir; çalışma
zamanında seçili deployment ve şema ile kanıt gerekir.

## Kod değişikliği yapılırsa yerleşim

| Değişiklik | Önce yeniden kullanılacak yer | Test yeri |
|---|---|---|
| Provider wire map | `Infrastructure/Llm/Providers/` | `ProviderAdapterTests.cs` |
| Retry, timeout, breaker | mevcut DI ve resilience düzeni | `ProviderResilienceTests.cs` |
| Capability veya revision çözümü | `ProviderClientCatalog` ve resolver | Infrastructure, sonra PostgreSQL integration |
| Alert payload normalizasyonu | `Infrastructure/Alerts/` | `AlertAdapterTests.cs`, `AlertTenantBindingTests.cs` |
| Alert HTTP ve JWT sınırı | `Api/Endpoints/AlertEndpoints.cs` | API HTTP integration |
| Metric veya log adapterı | `Infrastructure/Observability/Providers/` | provider unit ve `ContractTests` |
| Kalıcı provider release verisi | entity, configuration, repository | `IntegrationTests/ModelControlPlaneTests.cs` |

Yeni adapter, ancak envanterin gösterdiği ürün ihtiyacı için ayrı işte eklenir.
Application katmanına HTTP veya provider SDK bağımlılığı taşınmaz. Credential
sadece resolver ile çözülür.

Her deployment, tool calling, structured output, context, schema ve parametre
limitlerini doğrulanabilir descriptor ile bildirir. Activation/çağrı bu descriptorı
kontrol eder; model adına göre feature tahmini yapmaz. Local/self-hosted endpoint
credential olmadan izinli olabilir, ancak endpoint trust policy admin onayını ve
metadata/service adreslerine erişimin engellenmesini zorunlu kılar. Cloud fallback
varsayılan kapalıdır; açık policy, data-residency allowlist ve aggregate budget
olmadan devreye girmez. Local compatible endpoint contract ve native Gemini wire
fixtureları planlanmış testlerdir, mevcut implementasyon kanıtı değildir.

## Kod ve test kuralları

- Beklenen hata için `Result<T>` ve `ErrorCode` kullanın; yeni hata adı uydurmayın.
- Saat için `TimeProvider` kullanın; `DateTimeOffset.UtcNow`, `Thread.Sleep` ve sync wait kullanmayın.
- Outbound HTTP `IHttpClientFactory` ile gider; adapter testinde stub handler kullanılır.
- Yeni wire veya HTTP DTOsu source generated `JsonTypeInfo` ve uygun JSON context kaydıyla gelir.
- Test davranışın katmanında kalır; yeni paket veya proje gerekirse ilgili recipe ve lockfile akışı izlenir.
## Ortak provider adapter sözleşmesi

Her gerçek adapter, aynı case setini aynı fixture biçimiyle geçmelidir.
`HttpMessageHandler` stub ağsız wire testi içindir; gerçek credential veya internet
kullanmaz.

| Girdi veya uyarıcı | Assertion | Seviye |
|---|---|---|
| 200 structured cevap ve usage | metin, observed model, finish reason, usage ve güvenli metadata map edilir | Infrastructure unit |
| 400, 401, 429, 5xx | stable error taxonomy; ham response veya credential sızmaz | Infrastructure unit |
| Boş, malformed, truncated veya schema-invalid çıktı | success olmaz; usage varsa accounting korunur | Adapter ve gateway |
| Her adapter türü | ortak contract geçer; wire farkı fixture içindedir | Parametreli unit |
| Credential ve outbound header | secret log, exception, audit veya serialized nesnede yoktur | Security unit |
| CancellationToken | HTTP iptal edilir; ikinci çağrı veya sessiz success yoktur | Infrastructure unit |
| 429 ve Retry-After | bounded retry, kesin request sayısı ve süre bütçesi | Resilience unit |
| 503 veya transport hatası | HTTP pipeline retry tüketir; gateway tekrar retry etmez | Resilience unit |
| Failure threshold | breaker açılır; sonraki çağrı handlera gitmez | Resilience unit |

`ProviderResilienceTests.cs` bugün Retry-After, 429, 503, transport hatası,
attempt timeout, caller cancellation ve circuit-breaker davranışını kapsar. Yeni
resilience davranışı bu suitee eklenir; ikinci paralel retry katmanı kurulmaz.

Capability doğrulaması model adına göre yapılmaz. Pinned revision veya deployment
üzerindeki istenen descriptor, gerçek verification çağrısının sonucu ile eşleştirilir.
Structured-output kanıtı izinli şema, parse ve şema sonucu, observed model veya
revision ve redakte edilmiş request id içerir. Tool calling istenirse önce ürün
kapsamı kararı alınır; native çağrı platform gateway dışına taşınmaz.

## Alert ingress ve tenant sınırı

`POST /v1/alerts/{source}`, `WriteIncidents` yetkisi ister. Tenant doğrulanmış
principalden gelir; payload `AlertNormalizer.BindTenant` ile bağlanır.

| Girdi | Assertion | Seviye |
|---|---|---|
| Geçerli source payload ve yetkili JWT | canonical alert kuyruklanır, source korunur | HTTP integration |
| JWT yok, geçersiz veya policy yetersiz | 401 veya 403; publisher çağrılmaz | HTTP integration |
| JWT tenant A, payload tenant B | 403; kayıt ve event yok | HTTP integration ve unit |
| Tenant etiketi yok | JWT tenantı eklenir, diğer etiketler korunur | Unit |
| Bilinmeyen source veya malformed payload | güvenli 4xx, yan etki yok | HTTP integration |
| Aynı tenant tekrar gönderimi | mevcut idempotency veya dedup sözleşmesi korunur | Integration |

HTTP integration testi gerçek provider çağırmaz. Test host, test JWT imzalama
anahtarı ve mevcut in-memory publisher veya test double kullanılır. Tenant route
veya request bodyden güvenilir kabul edilmez.

## Veri kaynağı, metrik ve log kanıtı

Prometheus ve Loki testleri, modelden gelen değerlerin query diline kaçmasını
engelleyen allowlist ve escape sınırını gösterir. Her mapping şu ayrımı kanıtlar:

| Durum | Assertion | Yer |
|---|---|---|
| Geçerli tool ve label | shipped artifacttan beklenen query ve dar matcher | Provider unit |
| Geçersiz label veya ad | backend HTTP çağrısı yok, açık tool hatası | Provider unit |
| Veri yok | boş sonuç ve no-data semantiği | Provider unit veya contract |
| Backend 4xx, 5xx veya timeout | empty evidence değil, unavailable veya failed tool call | Provider unit |
| Partial veya warning cevap | partial semantiği ve kaynak metadata korunur | Provider unit |
| Gerçek backend fixture | pencere, sıra, provider adı ve cancellation | Contract integration |

`PrometheusMetricsProviderTests.cs` ve `LokiLogProviderTests.cs` mevcut HTTP stub
desenidir. `MetricsProviderContract.cs` aynı sözleşmeyi fake ve Testcontainers
Prometheus üzerinde çalıştırır. Yeni datasource önce yalnız iki backendin de dürüstçe
karşılayabildiği assertionlarla bu contractı kullanır; fark zayıflatılmaz, finding
olarak kaydedilir.

## Hassas veri sınırı

Fixturelar sahte credential, sentetik JWT, redakte alert ve sentetik prompt kullanır.
Her değişiklikte aşağıdakiler assertion olarak yer alır:

- raw API key, Authorization header, çözülmüş credential, ham prompt, ham provider
  cevabı veya kişisel veri log, span, audit ve artifactta yoktur;
- span alanları `TraceAttributes` ve `SetSafeTag(...)` ile yazılır;
- evidence kaynak, zaman penceresi, query kimliği ve güvenli hata bilgisini taşır;
  no-data ile unavailable birleştirilmez;
- incident snapshot immutable provider revision, deployment veya release hash taşır;
  secret taşımaz.

## Komutlar ve kanıt toplama

Değişiklik varsa standart gate şudur:

```bash
bash eng/ci.sh
bash eng/ci.sh --with-integration
```

Windows eşdeğeri:

```powershell
powershell -ExecutionPolicy Bypass -File eng/ci.ps1
pwsh -File eng/ci.ps1 -WithIntegration
```

Normal gate normal testleri ve scripted eval traitini çalıştırır. Integration
seçeneği Docker gerektiren `SreAiAgent.IntegrationTests` ile
`SreAiAgent.ContractTests` suitelerini çalıştırır. `--live-eval`, ancak açıkça
istenmiş gerçek provider doğrulamasında kullanılır; eksik credential, capability
veya endpoint skip değil failure olur. Bu belge hiçbir komutun çalıştığını veya
geçtiğini iddia etmez.

## Release evidence ve kabul kapısı

Her aday veya revision için redakte edilmiş kayıt şeması:

```text
integration: openai | openai-compatible | azure-openai | anthropic | ingress | datasource
candidate_revision: immutable id veya version
config_hash: sha256:<secret olmayan hash>
tested_at_utc: ISO-8601
code_revision: git commit veya build id
environment: test | staging | production-like
test_cases: test adı veya artifact yolu
result: passed | failed | blocked | unknown
observed_identity: izinli provider veya model veya revision alanları
evidence_refs: TRX veya JUnit, trace id, redakte request id
limitations: canlı doğrulama veya capability kapsamı
```

Kabul için matris güncellenir. Her doğrulandı satırı test adı ve tarihle izlenir.
Her ilk sürümde desteklenir satırı normal gate, uygun integration gate, security
assertionları ve release evidence ile bağlıdır. Canlı provider kanıtı gerekiyorsa
opt-in live eval geçer. Failed, blocked veya unknown, desteklenir durumuna
yükseltilmez.

## ADP-003 sonrası ayrı işler

Envanter bir boşluk gösterirse ayrı iş açılır: eksik HTTP integration host veya fake,
capability verification, live provider matrisi, datasource contract kapsamı, runbook
veya CI jobu. ADP-003 bu işleri hemen uygulamayı ve yeni sağlayıcı eklemeyi emretmez.