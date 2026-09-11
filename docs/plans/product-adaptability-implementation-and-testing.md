> Durum: Planlandı. Bu belge, mevcut kod veya testin tamamlandığını iddia etmez.
> Kapsam: ADP-001–ADP-605 ürün uyarlama uygulama ve test planı.
> Odak ek: [ADP-003 entegrasyon desteği ve test rehberi](adp-003-integration-support-and-testing.md).
> Tarih: 2026-09-10.

# Ürün uyarlanabilirliği — uygulama ve test rehberi

## Amaç ve kapsam sınırı

Platform alarmı alır, immutable configuration snapshot pinler, salt-okunur uzman
ajanlarla evidence toplar ve kök neden raporu üretir. Amaç her ürün veya LLM için
aynı sonucu vaat etmek değildir. Amaç, tenant, product ve environment sınırları
içinde destek matrisiyle ilan edilen capabilityleri kanıtlanmış biçimde sunmaktır.

ADP aşamaları backlog'dur. Bir işin burada olması implementasyon veya production
desteği değildir. Durum yalnız planlandı, uygulandı, kanıtlandı veya kanıt yoksa
bilinmiyor olur.

| Kural | Uygulama |
|---|---|
| Kimlik kapsamı | Kayıt ve erişim tenant + product + environment ile değerlendirilir. Product/environment ayrı erişim veya budget sınırıysa tenant sınırına eklenir. |
| Credential | Raw secret HTTP, DB, snapshot, log, span, audit veya artifact'a girmez; yalnız secret-store credential reference çözülür. |
| Yetki | Route, body ve prompt yetki kaynağı değildir; doğrulanmış principal ve policy kullanılır. |
| Budget | Provider/product/environment/tenant kotası gateway ve policy ile zorlanır; prompt metni mekanizma değildir. |
| Destek | Capability kapalıysa açık hata döner; fake production sonucu veya sessiz fallback yoktur. |
| Arayüz | UI opsiyoneldir. Onboarding, teşhis, activate ve rollback API/CLI-first tasarlanır. |

## Mevcut temel ve yeniden kullanım

| Alan | Mevcut kanıt | Planın yönü |
|---|---|---|
| Provider adapterları | src/SreAiAgent.Infrastructure/Llm/Providers/: OpenAI, OpenAI-compatible, Azure OpenAI, Anthropic | Yeniden kurmaz; product/env için contract ve live kanıt ister. |
| Provider testleri | Tests/SreAiAgent.Infrastructure.Tests/ProviderAdapterTests.cs, ProviderResilienceTests.cs | Ortak wire/resilience baseline'ı. |
| Control plane | Application/Abstractions/Llm/IModelProviderControlPlane.cs, Infrastructure/Persistence/Repositories/ModelProviderRepository.cs | Revision ve assignment sınırını kullanır. |
| Snapshot | ConfigurationPinner.cs, ConfigurationSnapshotStore, ModelAssignmentJson | Incident pinning'ini product/env bağlamında doğrular. |
| Ingress | src/SreAiAgent.Api/Endpoints/AlertEndpoints.cs | Principal tabanlı tenant sınırını korur. |
| Datasource | Prometheus/Loki provider testleri ve contract düzeni | Yeni integration gerçek sözleşmeye uyar. |

Scripted provider yalnız deterministic development/eval içindir; gerçek ürün
desteğinin kanıtı değildir. Native provider tool calling kapsamda değildir:
read-only tool çağrıları policy-enforced platform gateway'den geçer.

Dört mevcut adapter başlangıç baseline'ıdır, hedef limit değildir. Registry, kullanıcı
tarafından tanımlanan model kimlikleri ve provider endpoint/deployment revision'larını
taşır; model kimliği hard-coded enum olmaz. Aynı Qwen kimliği yerelde veya hosted
endpointte farklı deployment olabilir. Kimi bir model ailesi veya servis olabilir;
protokolü evrensel kabul edilmez. Gemini gibi farklı native API gerektiğinde ayrı
adapter gerekir. Compatible endpoint yalnız bir abstraction'dır; her runtime
capabilitysi endpoint ve deployment üzerinde ayrıca doğrulanır.

## Teknik kurallar

- Beklenen hata Result<T> ve mevcut ErrorCode ile döner; invariant dışı hata
  DomainException olabilir.
- Saat TimeProvider.GetUtcNow() ile alınır. HTTP IHttpClientFactory; logging
  LoggerMessage; telemetry TraceAttributes ve SetSafeTag(...) kullanır.
- Serialization source-generated JsonTypeInfo context'iyle yapılır. Snapshot veya
  Temporal değişiminde WorkflowPayloadJson uyumluluğu aynı iştedir.
- Yeni paket/proje gerekiyorsa lockfile tool ile üretilir; elle düzenlenmez.
  Test doğru katmanda anlamlı davranışı sınar, implementasyonu mirror etmez.
- Raw prompt, credential, authorization header ve ham provider cevabı span, log,
  audit veya artifact'a yazılmaz.

## Kanıt türleri

| Tür | Kanıtladığı | Kanıtlamadığı |
|---|---|---|
| Deterministic unit | Parse, mapping, policy, hata, limit | Gerçek endpoint erişimi |
| HTTP/API integration | Auth, principal scope, persistence, yan etkisizlik | Gerçek LLM/datasource kalitesi |
| Contract/Testcontainers | Gerçek altyapıda ortak sözleşme | Seçili müşterinin live erişimi |
| Opt-in live | Gerçek target, revision, capability, observed identity | Tüm kombinasyonlara destek |
| Pilot | Kalite, güvenilirlik, operasyon eşiği | Başka product/env desteği |

Live koşu açıkça seçilir; credential, endpoint veya capability yoksa skip yerine
failed ya da blocked kaydedilir. Secret içermeyen evidence kaydı product, tenant
scope, environment, candidate revision, snapshot hash, code revision, input/fault,
assertion, observed identity, threshold, artifact referansı, limit ve owner taşır.

## AI ajanı yürütme protokolü

Her ajan yalnız bir aşamada çalışır. Aşama girişinde mevcut durumu inceler, mevcut
adapter/control-plane/snapshot/testi yeniden kullanır ve yeniden inşa etmez. Önce
küçük bağımlılığı kapalı implementasyon yapılır; sonra riskine uygun deterministic,
integration, contract veya açıkça seçilmiş live kontrol çalıştırılır. Evidence ve
plan durumu ancak sonuç kaydedildikten sonra güncellenir.

Aşama giriş şartı önceki aşamanın gerekli decision/evidence referanslarının
olmasıdır. Aşama çıkış şartı bu bölümün deliverable, acceptance ve test matrisinin
kanıtlanmasıdır. Gelecek aşamalar tamamlandı diye işaretlenmez. Kod değişiklikleri
bu belge nedeniyle otomatik commit edilmez; commit ayrı kullanıcı/depo iş akışıdır.

~~~mermaid
flowchart LR
 P0[ADP 0 hedef/matris] --> P1[ADP 1 scope/snapshot]
 P0 --> P2[ADP 2 integration contract]
 P1 --> P3[ADP 3 provider/budget]
 P2 --> P3 --> P4[ADP 4 onboarding]
 P4 --> P5[ADP 5 iki pilot] --> P6[ADP 6 readiness]
~~~

## Aşama 0 — hedef, kapsam ve destek matrisi

İşler: ADP-001, ADP-002, ADP-003, ADP-004, ADP-005. Durum: planlandı. Bu aşama
mevcut adapter veya snapshot'ı yeniden yazmayı emretmez.

| İş | Çıktı | Yeniden kullanım/değişiklik |
|---|---|---|
| ADP-001 | Product/tenant/environment sözlüğü, ownership, scope | Mevcut JWT tenant sınırı; doküman/karar |
| ADP-002 | Hedef, kapsam dışı, başarı ölçüsü | README ve ARCHITECTURE; doküman |
| ADP-003 | Integration/capability/support evidence matrisi | ADP003 odak eki; matris |
| ADP-004 | İki pilot selection record | Config ve alert akışı; karar |
| ADP-005 | Adapter-vs-config triage | Provider catalog/control plane; karar/backlog |

Pilot isimleri ve gerçek target'lar verilmemiştir; isim uydurulmaz. Selection
record product id, tenant, environment, alert source, güvenli datasource listesi,
owner, veri sınıfı, hacim, model capability, failure budget, rollback sahibi ve
önceden ilan edilmiş kalite eşiğini taşır. Fixture ile kodlanabilmek live pilot
kapısını kaldırmaz: iki gerçek product/target gerekir.

| Değişiklik | Karar |
|---|---|
| Endpoint, deployment/revision, credential reference, limit | Konfigürasyon/revision |
| Wire formatı, auth protokolü, semantik capability | Adapter |
| Label, namespace, query allowlist | Scoped product/environment konfigürasyonu |
| Yeni tool yetkisi veya veri sınıfı | Policy + operasyon kararı |

| Input/fault | Assertion | Katman |
|---|---|---|
| Matris capability satırı | Product/env, revision, kanıt, owner, durum dolu | Doküman incelemesi |
| Desteksiz integration | supported değil disabled/unknown görünür | Matris |
| Pilot seçimi | İki target ve threshold selection record'da | Operasyonel karar |
| Adapter talebi | Triage gerekçesi kayıtlı | Tasarım incelemesi |

Giriş: yok. Çıkış/kabul: sürümlü destek matrisi, iki target için selection record,
ADP003 evidence formatı. Bunlar yoksa Aşama 1'e geçilmez.

Destek matrisi provider/protocol adapter, model ID ve endpoint/deployment'ı ayrı
sütunlarda gösterir. Registry isteğe bağlı provider discovery kullanabilir; discovery
olmayan veya desteklenmeyen target için manual model ID girişi vardır. Kayıtlar
silinmez: enable, disable ve archive edilir; pinned snapshot referansları geçerli
kalır. Global default, product/environment ve agent assignment açık öncelik sırasıyla
belgelenir. Draft → test/verify → activate revision akışı immutabledır; in-flight
workflow'a hot mutation uygulanmaz.

## Aşama 1 — product/environment control plane ve snapshot uyumluluğu

İşler: ADP-101, ADP-102, ADP-103, ADP-104, ADP-105. Durum: planlandı.

| İş | Planlanan değişiklik | Mevcut temel |
|---|---|---|
| ADP-101 | Product/environment entity, API, scope policy | Tenant principal/control-plane abstractions |
| ADP-102 | Credential reference yaşam döngüsü | Provider revision modeli |
| ADP-103 | Scoped model assignment | IModelProviderControlPlane, repository |
| ADP-104 | Versioned immutable snapshot alanları | ConfigurationPinner, ModelAssignmentJson |
| ADP-105 | Backward migration, isolation, Temporal compatibility | Migrations, WorkflowPayloadJson |

Product/environment global anahtar gibi kabul edilmez. Create/read/update/activate
principal scope'u doğrular. Published revision değişmez; yeni version oluşur. Eski
snapshot resume olduğunda yeni active assignment'a kaymaz.

| Input/fault | Assertion | Katman |
|---|---|---|
| Tenant A/B kaydı | Read, reference, activate reddedilir | API + repository integration |
| Aynı tenant, farklı product/env | Scope dışı model/credential çözülmez | Application + integration |
| Raw credential | API/DB/snapshot reddeder; log/audit sızdırmaz | API + security unit |
| Revision değişimi | Eski satır sabit, yeni id/version/hash | Domain/repository |
| Eski snapshot/Temporal payload | Deserialize olur; optional required member nullable uyumlu | Workflow serialization |
| Migration/cutover | PostgreSQL round-trip; önceki veri korunur | Testcontainers integration |

Giriş: Aşama 0 sözlüğü ve scope kararı. Çıkış/kabul: scope şeması, version/migration
planı, API/CLI sözleşmesi ve compatibility evidence. UI zorunlu değildir.

## Aşama 2 — ortak integration contract ve güvenli normalizasyon

İşler: ADP-201, ADP-202, ADP-203, ADP-204, ADP-205, ADP-206. Durum: planlandı.

| İş | Planlanan değişiklik | Sınır |
|---|---|---|
| ADP-201 | Ortak contract/capability descriptor | Product code Application'a taşınmaz |
| ADP-202 | Metric label ve log namespace mapping | Allowlist dışı query yok |
| ADP-203 | Ingress auth + product/env normalizasyonu | Principal scope body'den gelmez |
| ADP-204 | Süre/seri/payload/concurrency limits | Gateway enforce eder |
| ADP-205 | No-data, unavailable, partial, conflict semantiği | Boş evidence başarı değildir |
| ADP-206 | Gerekirse adapter extension | Fake production adapter yok |

Prometheus/Loki provider ve contract testleri ilk referanstır. Mapping scoped
versioned konfigürasyondur. Query model serbest metninden değil tool schema ve
allowlist'ten kurulur.

| Input/fault | Assertion | Katman |
|---|---|---|
| Geçerli label/namespace | Dar matcher ve izinli query | Provider unit |
| Bilinmeyen label/tool | Backend HTTP yok; güvenli hata | Provider unit |
| JWT/body scope çelişkisi | 401/403 veya uygun hata; yan etki yok | HTTP integration |
| No data | no-data ile unavailable birleşmez | Contract/unit |
| 4xx/5xx/timeout/partial | Failure/partial metadata korunur | Contract + resilience |
| Limit aşımı | Gateway request'i keser | Application/unit |

Giriş: Aşama 0 triage; kalıcı scope gerekiyorsa Aşama 1. Çıkış/kabul: mapping
şeması, normalizasyon/error semantiği, query limitleri ve datasource başına
deterministic contract evidence.

## Aşama 3 — provider seçimi, capability, budget ve rotation

İşler: ADP-301, ADP-302, ADP-303, ADP-304, ADP-305, ADP-306. Durum: planlandı.

| İş | Planlanan değişiklik | Mevcut temel |
|---|---|---|
| ADP-301 | Product/env/agent provider-model seçimi | Model assignment release |
| ADP-302 | Capability activation validation | Adapter contract |
| ADP-303 | Açık fallback/disabled davranışı | Result ve ErrorCode sınırı |
| ADP-304 | Gateway-enforced budget | LLM gateway policy sınırı |
| ADP-305 | Credential rotation/revision cutover | Credential reference, immutable revision |
| ADP-306 | İki gerçek provider için opt-in evidence | Dört mevcut adapter |

Seçim descriptor ve pinned revision ile yapılır. Parametre düşürmek, başka model
seçmek veya scripted'a dönmek yasaktır. Ürün onaylı fallback varsa hedef,
capability kaybı, budget ve audit kaydı tanımlıdır; yoksa açık hata döner. Rotation
yeni revision üretir; eski snapshot çözülemezse açık hata üretir.

Registry her deployment için tool calling, structured output, context, schema ve
provider parametre limitlerini descriptor olarak saklar; capability model adına
bakılarak tahmin edilmez. Local/self-hosted endpoint credential gerektirmeyebilir;
cloud endpoint için credential reference çözülür. Rotation yeni revision ve reverify
ister. TLS, local network ve endpoint trust policy açık konfigürasyondur. Admin
onaylı local/self-hosted endpoint izinli olabilir; bu, kullanıcının metadata veya
service addresslere sınırsız ağ erişimi kazanması değildir.

Cloud fallback varsayılan olarak kapalıdır. Açılması, explicit fallback policy,
data residency için izinli provider listesi ve aggregate budget ister.

| Input/fault | Assertion | Katman |
|---|---|---|
| İki scoped assignment | Her agent doğru pinned adapter/modeli çözer | Application + integration |
| Eksik slot/capability | Activation atomik reddedilir; fallback çağrısı yok | Control-plane integration |
| 200 structured output | Observed identity, usage, safe metadata map edilir | Adapter unit |
| 401/429/5xx/cancel | Stable taxonomy; secret/raw response yok | Resilience unit |
| Budget dolu | Outbound çağrı yok; audit/metric güvenli | Application/integration |
| Rotation eski/yeni snapshot | Incident kendi revision davranışını korur | Workflow/repository integration |
| İki gerçek provider | Onaylı target capability smoke, redakte request id | Opt-in live |
| Aynı model, iki endpoint | Yeni revision/capability; eski workflow eski snapshotta | Integration + workflow |
| Local compatible endpoint | Fake/contract fixture güvenli query ve runtime capabilityyi doğrular | Proposed contract |
| Native Gemini protocol | Ayrı wire fixture capability/hata mapini doğrular | Proposed unit fixture |
| Discovery desteklemez | Manual model ID draft/test akışından geçer | Control-plane integration |
| Unsupported feature | Activation veya çağrı typed hata verir; fallback yok | Contract/unit |

Giriş: Aşama 1 scope/snapshot, Aşama 2 contract, iki gerçek provider target'ı.
Çıkış/kabul: selection/activation evidence, budget/rotation runbook taslağı, iki
gerçek provider opt-in kanıtı. Adapter dosyası live kanıt değildir.

## Aşama 4 — API/CLI-first onboarding ve operasyon akışı

İşler: ADP-401, ADP-402, ADP-403, ADP-404, ADP-405, ADP-406. Durum: planlandı.

| İş | Çıktı |
|---|---|
| ADP-401 | Scoped product/env config API/CLI |
| ADP-402 | Secret içermeyen connectivity/capability diagnostics |
| ADP-403 | Test-alert → evidence → report draft |
| ADP-404 | Evaluate/activate ve audit/evidence |
| ADP-405 | İki product onboarding runbook örneği |
| ADP-406 | Immutable release rollback ve operatör teşhisi |

Connectivity yalnız DNS/200 değildir: auth scope, allowlisted query, minimum
anlamlı veri, no-data/partial ayrımı ve capability sonucunu raporlar. Test alert
sentetik olabilir; gerçek pilot gate'i gerçek target ve Aşama 0 eşiğine bağlıdır.

Management API/CLI, arbitrary model ID, endpoint/deployment, credential reference,
capability descriptor ve revision durumunu yönetir. Discovery yalnız kolaylıktır;
manual entry eşdeğer yoldur. Endpoint policy, onaylı local target'ı desteklerken
unauthorized metadata/service address ve trust policy dışı egressi reddeder.
Offline endpoint startup/health teşhisi typed failure döndürür; local endpoint için
auth varsayımı yapılmaz. UI eklenirse bu API/CLI akışının istemcisidir.

| Input/fault | Assertion | Katman |
|---|---|---|
| Yetkili/yetkisiz API/CLI | Scope uygulanır; command output secret içermez | HTTP integration/CLI |
| Bozuk endpoint/reference | Güvenli teşhis ve sonraki adım | Integration + opt-in live |
| Test alert | Canonical alert, evidence ref, report draft | End-to-end integration |
| Anlamlı veri yok | Başarılı RCA gibi görünmez | Contract + e2e |
| Activate | Verify/eval/capability olmadan pointer değişmez | Control-plane integration |
| Rollback | Önceki immutable release; yeni incident seçimi kanıtlı | Integration + workflow |
| Cross-product credential | Bir product/env credential reference'ı diğerinde çözülemez | API + integration |
| Endpoint policy | Onaylı local kabul; unauthorized metadata/service adresi reddedilir | Security integration |
| Offline endpoint | Startup/health typed failure; secret sızmaz | Contract/integration |
| Discovery/manual entry | Unsupported discovery sonrası manual ID ile draft/test mümkündür | Control-plane integration |

Giriş: Aşama 1–3, iki örnek için gerçek karar. Çıkış/kabul: API/CLI reference,
redakte diagnostics, iki onboarding örneği, evidence/report akışı, rollback
runbook. UI eklenirse bu API'nin istemcisidir.

## Aşama 5 — iki gerçek pilotla kalite ve güvenilirlik

İşler: ADP-501, ADP-502, ADP-503, ADP-504, ADP-505. Durum: planlandı.

Bu, production-readiness.md içindeki eski Phase 5 değildir. Buradaki ADP Aşama 5
iki gerçek product pilotunun outcome evidence'ıdır. Eski readiness Phase 5 kanıtı,
Aşama 6'nın giriş koşuludur.

| İş | Pilot kanıtı |
|---|---|
| ADP-501 | RCA quality rubric ve evidence grounding |
| ADP-502 | Evidence reliability, erişim, citation doğruluğu |
| ADP-503 | Missing/conflicting data ve uncertainty |
| ADP-504 | Partial outage, latency, cost/budget |
| ADP-505 | Hallucination, prompt injection, leak negatif testleri |

Threshold'lar Aşama 0 record'unda product başına tanımlıdır: grounded-RCA oranı,
citation coverage, yasak iddia sıfır, no-data/conflict kalibrasyonu, p95 latency,
maliyet ceiling, hata oranı ve security ihlali sıfır. Bir pilot diğerine genellenmez.

| Input/fault | Assertion | Katman |
|---|---|---|
| Gold evidence incident | RCA ailesi, citation, uncertainty rubric | Deterministic eval + pilot |
| Eksik veri | Desteksiz iddia yok; missing evidence görünür | Eval/contract |
| Çelişkili veri | Çelişki/kaynak görünür; doğru uydurulmaz | Eval/pilot |
| Partial outage | Report partial/unavailable; fake success yok | Failure injection/e2e |
| Yüksek latency/cost | p95/budget eşiği ve policy enforcement ölçülü | Load/live evidence |
| Injection/secret fixture | Tool policy ihlali ve leak sıfır | Security eval |

Giriş: Aşama 4, iki gerçek target, redakte artifact kuralları ve eşikler.
Çıkış/kabul: pilot başına immutable candidate tuple, evidence artifact, threshold
karşılaştırması, eksik/çelişkili veri sonucu ve rollback kararı. Eşik geçilmezse
failed/blocked kalır.

## Aşama 6 — readiness, recovery ve işletim dayanıklılığı

İşler: ADP-601, ADP-602, ADP-603, ADP-604, ADP-605. Durum: planlandı.

| İş | Kanıt |
|---|---|
| ADP-601 | Eski readiness Phase 5 tekrar kontrolü, replay/load/noisy-neighbor/budget |
| ADP-602 | PostgreSQL, Kafka, Temporal recovery/migration |
| ADP-603 | Kubernetes CNI, secret delivery, network policy |
| ADP-604 | Safe product/env tracing, metrics, logs ve correlation |
| ADP-605 | Support matrix, runbook, ownership/on-call handoff |

Readiness compile veya unit testten ibaret değildir. Alarm teslimi, inbox/dedup,
snapshot pinning, workflow resume, gateway limiti ve evidence zinciri altyapı
failure'larında görünür olmalıdır. Telemetry product/env etiketleri allowlisted ve
düşük-cardinality olur; raw query, prompt veya secret etiket olmaz.

| Input/fault | Assertion | Katman |
|---|---|---|
| Replay/duplicate alert | Idempotency/dedup; product/env karışmaz | Integration/stack |
| Load/noisy neighbor | Bir scope bütçeyi tüketince diğeri izole | Load/stack |
| PG/Kafka/Temporal kesintisi | Recovery sonrası pinned snapshot/side effect korunur | Recovery/stack |
| Migration/restore | Forward/backward uyum ve runbook | PostgreSQL integration |
| CNI/secret erişimi | Yetkisiz egress/read engelli, teşhis edilebilir failure | K8s opt-in |
| Trace/log/metric | Correlation, safe labels, hassas veri sıfır | Telemetry/security |

Giriş: ADP Aşama 5 pilot evidence ve eski readiness Phase 5 kanıtı. Çıkış/kabul:
replay/load/recovery raporu, k8s/secret/tracing evidence, güncel support
matrix/runbook. Kanıt sadece ölçülen product/env/revision için geçerlidir.

## Test komutları ve evidence güncellemesi

Kod veya process-boundary değişikliğinde standart gate:

~~~bash
bash eng/ci.sh
bash eng/ci.sh --with-integration
~~~

Windows eşdeğeri:

~~~powershell
powershell -ExecutionPolicy Bypass -File eng/ci.ps1
pwsh -File eng/ci.ps1 -WithIntegration
~~~

İkinci komut Docker ister. Stack doğrulaması için
[verify-end-to-end.md](../agents/recipes/verify-end-to-end.md) izlenir. Canlı
provider doğrulaması açıkça seçilmiş opt-in koşudur; ADP003 ekindeki live-eval
sınırı ve evidence formatı kullanılır. Bu rehber hiçbir komutun geçtiğini iddia
etmez.

Uygulama sırası: Aşama 0 kararları → Aşama 1 scope/snapshot → Aşama 2 contract
→ Aşama 3 provider/budget → Aşama 4 onboarding → Aşama 5 pilot → Aşama 6
recovery. Kalıcı mimari tercihi gerekçesi ve maliyetiyle docs/DECISIONS.md'ye
taşınır. Ana rehber her artırımda evidence referanslarıyla güncellenir; ADP003
odağı bunun yerine geçmez.
