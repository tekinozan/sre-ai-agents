> **Durum:** P0–P6 uygulandı; P4-5 (secret store) kullanıcı kararıyla ertelendi. Ayrıntı her satırın kanıt sütununda.
> **Kapsam:** Projenin 2026-10-05 itibarıyla faz, sağlayıcı esnekliği, production
> hazırlığı ve iş akışı değerlendirmesi ile bulguların öncelikli düzeltme sırası.
> **Kapsam dışı:** Bu belge `adp-phased-execution-plan.md` veya
> `production-readiness.md`'nin yerine geçmez; onların açık adımlarını yeniden
> tanımlamaz, yalnızca hangisinin önce yapılacağını ve bu değerlendirmenin
> bulduğu yeni boşlukları kaydeder.
> **Son güncelleme:** 2026-10-05.

# Proje değerlendirmesi ve öncelikli düzeltme planı (2026-10)

## İlerleme

Durum yalnız şu değerleri alır: `planlandı`, `uygulandı`, `doğrulandı`, `bloklu`.
Bir satır, komut çalıştırılıp kanıt yazıldıktan sonra güncellenir.

| Adım | İş | Durum | Kanıt |
|---|---|---|---|
| P0-1 | Plan doküman kayması: ADP ilerleme tablosu, eksik `PRODUCTION_READINESS_PLAN.md` referansı | uygulandı | ADP tablosu F1-1…F1-5 commit referanslarıyla; readiness kaynağı bu repodaki belgeye çevrildi (kök dosya hiç commit edilmemiş) |
| P0-2 | Gate Linux'ta kırmızı: eval harness platforma bağlı | doğrulandı | Endpoint doğrulaması Unix'te `/v1/...`'i `file://` sayıyordu; scorer hash'i `Environment.NewLine`'lı girintili JSON'dan hesaplanıyordu. Canonical hash + Linux'ta kaydedilmiş `scripted-smoke-2026.10.05.json`; Evals 43/43 |
| P1-1 | BUG-001: fingerprint yarışı ve "resolved" teslimatı | doğrulandı | D42, D43. Gate `--with-integration`: 454 + eval + 46 + 11 test yeşil. Compose: tek alert → `Resolved`/`Complete`; 8 eşzamanlı aynı alert → 1 incident, sayaç 8; bilinmeyen kimliğe resolved teslimat → 0 incident; tenant'sız zehirli mesaj 5 kez yeniden teslim edildi ve dead-letter'a gitti, arkasındaki alert işlendi; `sre_trace_attribute_rejected_total` 0 |
| P1-2 | "Resolved" anlamının ayrılması: soruşturma bitti ≠ kesinti bitti | doğrulandı | D43 cooldown: Resolved incident'a pencere içinde gelen alert sayılır, yeniden soruşturulmaz (compose: sayaç 2, tek `investigation.requested`). Status adının değişmesi bilinçli olarak reddedildi; maliyeti D43'te |
| P1-3 | BUG-002: `TenantWide` assignment'ı bırakmayan aktivasyonun reddi | doğrulandı | D44; `ModelControlPlaneTests` kapanış kanıtı (a) ve (b), Postgres integration |
| P1-4 | Onay reddi veya süre aşımı RCA raporunu kaybetmez (V2 için gizli hata) | doğrulandı | `InvestigationSequenceTests`: red ve süre aşımında `persist` + `complete`, diğer hatalar hâlâ `fail`. V1 bu dala girmediği için replay işaretçisi gerekmedi |
| P1-5 | Reopen gerçek aktörü kaydeder | doğrulandı | Retry endpoint principal'ı geçirir; timeline girdisi aktörü taşır (`RetryInvestigationHandlerTests`) |
| P2-1 | GitHub Actions CI: gate + integration | uygulandı | `.github/workflows/ci.yml`: Linux gate `--with-integration`, Windows gate, Helm render kontrolü. `actionlint` temiz; yerel eşdeğerleri yeşil. İlk gerçek koşu PR açıldığında görülecek; branch protection ayarı repo sahibinde |
| P2-2 | Container image build, zafiyet taraması | uygulandı | Üç image build (push yok: yayın hedefi henüz kararlaştırılmadı), Trivy commit SHA ile sabit, düzeltilebilir CRITICAL bulguda kırmızı. Yerel tarama: 0 bulgu |
| P3-1 | Servisler arası trace sürekliliği (production-readiness sign-off blokeri) | doğrulandı | Kök neden Temporal sınırı: interceptor kaynakları tracer provider'a kayıtlı değildi; worker'daki ikinci interceptor span'leri ikiliyordu. Compose: tek alert → tek trace, kök `POST /v1/alerts/{source}`, 3 servis, her `RunActivity` bir kez; oran 0'da soruşturma `RunWorkflow:SreInvestigation` kökünde tam kalıyor |
| P3-2 | Readiness Faz 5: workflow replay, k6, backup/DR runbook | uygulandı | `WorkflowReplayTests` compose'dan kaydedilmiş geçmişle offline (kasıtlı sıra değişikliğini TMPRL1100 ile yakaladığı gösterildi); `eng/load/alert-storm.js` kısaltılmış koşu: eşikler geçti, alert kaybı yok; RUNBOOK yedekleme/geri yükleme (compose'da doğrulandı), rotasyon, retention, koordineli kurtarma, yük. Kalan: 25 dk tam soak, gerçek SASL/TLS broker |
| P4-1 | OpenAI: `max_completion_tokens`, reasoning modellerinde `temperature` | uygulandı | D45; `ProviderAdapterTests` gövde düzeyinde. Gerçek sağlayıcıya karşı canlı kanıt yok (credential yok, ADP F3-6 bloklu) |
| P4-2 | OpenAI-compatible structured output lehçesi (`json_schema` / `json_object`) | uygulandı | D45; `json_object` modunda şema prompt'ta, doğrulama gateway'de. Canlı kanıt yok |
| P4-3 | Kullanılmayan `tool_calling` gereksiniminin compatible sağlayıcıları elemesi | doğrulandı | D45; temiz volume'de bootstrap deployment'ları `required_capabilities = 3` (StructuredOutput, SystemMessage) ve soruşturma tamamlandı; mevcut volume'de bootstrap no-op |
| P4-4 | Native Gemini adapter (ADP F3-7) | uygulandı | D46; `gemini-budgets-notifications.md` G-1. Canlı kanıt credential bekliyor |
| P4-5 | Secret store credential resolver (Vault / ExternalSecrets) | planlandı | — |
| P5-1 | Kiracı başına LLM bütçesi ve alert fırtınası koruması (ADP F3-4) | doğrulandı | D47; `gemini-budgets-notifications.md` B-1…B-3. Compose: farklı para birimi 409; limit dolunca sağlayıcıya çağrı yok (`llm_calls` 0 maliyetli `BUDGET_EXCEEDED`), soruşturma `Resolved`/`BudgetExceeded`. Fırtına için ayrı bir hız limiti eklenmedi: D43 tekilleştirme + bütçe tavanı maliyeti sınırlıyor |
| P5-2 | Bildirim çıkışı (Slack/Teams/PagerDuty) | doğrulandı | D48; `gemini-budgets-notifications.md` N-1…N-3. PagerDuty kapsam dışı (kullanıcı kararı). Ön koşul olarak hiç yazılmayan `sre.incident.completed.v1` artık yayımlanıyor |
| P5-3 | `GET /v1/incidents` listeleme ve filtreleme | doğrulandı | `incidents-feedback-evals.md` L |
| P6-1 | RCA geri bildirim döngüsü | doğrulandı | D49; `incidents-feedback-evals.md` F |
| P6-2 | Eval tamamlamada görev ayrılığı ve artifact doğrulaması | uygulandı | D50; `incidents-feedback-evals.md` E-1. Tamamlama compose'da tek dev kimliğiyle gösterilemez; Postgres+HTTP integration ile kanıtlı |
| P6-3 | `llm-providers.md` Faz 4: baseline/promotion gate | uygulandı | D50; `incidents-feedback-evals.md` E-2. Opsiyonel LLM judge kapsam dışı (kalibrasyon seti yok) |

---

## 1. Mevcut durum ve faz

| Yol haritası | Durum (2026-10-05) |
|---|---|
| ARCHITECTURE.md §20 V1 (read-only agent'lar, RCA raporu, remediation yok) | Büyük ölçüde tamam: 3 expert, 6 read-only tool, Temporal, outbox/inbox, snapshot pinning, OTel |
| `llm-providers.md` | Faz 1–3 tamam; Faz 4 yok; Faz 5 kısmen |
| `production-readiness.md` | Faz 1–4 tamam; **Faz 5 başlamadı** |
| `adp-phased-execution-plan.md` | F0 doğrulandı; F1-2…F1-5 commit'te (`fe67c2a`…`60589f2`) ama tablo "planlandı" diyor; F1-6…F6 açık; F0-10/F3-6/F5-6 pilot hedefi yokluğundan bloklu |

Alert → evidence → RCA akışı uçtan uca çalışıyor ve Compose kanıtı var; ancak
varsayılan model scripted, tek gerçek pilot OpenAI 429'unda kesildi. Proje
**V1'in ortasında, pilot öncesi** aşamadadır.

## 2. Sağlayıcı ve model desteği

Bugün: `ProviderAdapterKind = Scripted | OpenAi | OpenAiCompatible | AzureOpenAi | Anthropic`;
credential yalnız `env://` veya `file://` referansı; production host allowlist'i
sabit (`api.openai.com`, `api.anthropic.com`, `*.openai.azure.com`), diğerleri
`AllowedCustomEndpointHosts` üzerinden.

| Sağlayıcı | Durum | Boşluk |
|---|---|---|
| Anthropic | Native adapter | Bedrock/Vertex yok; cache token maliyeti hesaplanmıyor |
| OpenAI | Chat Completions | `max_tokens` gönderiliyor; reasoning modelleri `max_completion_tokens` ister ve varsayılan dışı `temperature`'ı reddeder (P4-1) |
| Gemini | Yalnız OpenAI-compatible | Native wire yok (P4-4) |
| DeepSeek / Qwen / Groq | OpenAI-compatible | Her zaman `json_schema` + `strict: true`; DeepSeek yalnız `json_object` destekler, diğerlerinde modele bağlı (P4-2) |
| Tüm compatible sağlayıcılar | — | `analytical.v1.yaml` `tool_calling: true` istiyor; adapter'lar `tools` göndermiyor ama descriptor uyuşmazlığı aktivasyonu reddediyor (P4-3) |

Referans (`can1357/oh-my-pi`, `packages/ai/src`) üç katman ayırır: API lehçesi
(`anthropic-messages`, `openai-completions`, `openai-responses`,
`google-generative-ai`, `google-vertex`, `bedrock-converse-stream`), model
başına compat bayrakları (`maxTokensField`, `supportsStrictMode`,
`retryWithoutStrictOnGrammarError`, `reasoningContentField`, …) ve OAuth/havuz/
rotasyon/kota yöneten ayrı auth katmanı. Burada üçü tek bir capability bitmask'ine
sıkışmış durumda.

**Abonelik (OAuth) erişimi** bu tasarıma uymuyor: `ProviderConnectionRevision`
immutable ve statik `credential_ref` taşıyor; OAuth ise yenilenen token, hesap
havuzu ve geri yazılabilir depo ister. Tüketici aboneliklerinin (Claude Pro/Max,
ChatGPT Plus/Pro, …) şartları genellikle çok kiracılı bir sunucu arka ucu olarak
kullanıma izin vermez; hukuki inceleme olmadan production'a girmez ve bu planda
uygulanmaz. İş ihtiyacı "kiracıya plan/kota" ise karşılığı P5-1'dir.

## 3. Production hazırlığı boşlukları

1. `.github/workflows` yok: gate yalnız yerelde; image build/push, tarama, imza yok (P2).
2. Bir alert→rapor koşusu birden çok Jaeger trace id'sine dağılıyor (P3-1).
3. Readiness Faz 5 yok: replay testi, k6, backup/restore, rotasyon, DR runbook (P3-2).
4. BUG-001 (veri kaybı) ve BUG-002 (sessiz operasyonel kesinti) kodda açık (P1-1, P1-3).
5. Gerçek OpenAI çağrısı ve SASL/TLS Kafka canlı doğrulanmadı; ikinci gerçek sağlayıcı yok (ADP F3-6, bloklu).
6. Secret yalnız env/dosya: yeni kiracı bağlantısı redeploy ister (P4-5).
7. Eval tamamlama aktivasyonla aynı politikayla `passed=true` ve serbest `artifactHash` kabul ediyor (P6-2).
8. Harici LLM'e giden log/metrik içeriği yalnız regex redaction'dan geçiyor; sağlayıcı bölgesi için veri yerleşimi politikası gerekli (KVKK). Kod işi değil, karar işi; P4-5 ile birlikte ele alınır.

## 4. İş akışı boşlukları

1. **"Resolved" iki anlam taşıyor.** `CompleteInvestigationAsync` rapor yazılınca
   `MarkResolved` çağırıyor; dedup filtresi Resolved'u dışladığı için süren bir
   kesintinin her yeni alert'i yeni incident ve yeni LLM soruşturması açıyor
   (pilot: aynı fingerprint için 6 incident) (P1-2).
2. "resolved" teslimatı soruşturulmayan incident açıyor; sonraki gerçek alert'ler
   yalnız sayaç artırıyor (BUG-001 mekanizma 1, P1-1).
3. Onay reddi/süre aşımı `FailAsync`'e gidiyor ve rapor kalıcılaşmıyor; yorum
   tersini söylüyor. V1'de `DecideApproval` onay istemediği için tetiklenmiyor (P1-4).
4. Bildirim çıkışı yok (P5-2); incident listeleme yok (P5-3); RCA geri bildirimi yok (P6-1).
5. Kiracı başına bütçe ve fırtına koruması yok (P5-1).

---

## 5. Adımlar

### P0-1 — Plan doküman kayması

`adp-phased-execution-plan.md` ilerleme tablosunu commit'lerle hizala;
`production-readiness.md` içindeki var olmayan `PRODUCTION_READINESS_PLAN.md`
referansını düzelt. Kabul: tablo commit geçmişiyle çelişmez; kırık referans kalmaz.

### P0-2 — Linux gate

`bash eng/ci.sh` bu repoda Linux'ta hiç yeşil olmamıştı: proje Windows'ta
geliştirildi ve eval harness iki yerde platforma bağlıydı. İkisi düzeltildi;
eski baseline immutable olduğu için silinmedi, yenisi en güncel ad olarak seçilir.

Açık kalan, bilinçli olarak bu adıma katılmayan: `AgentSession` ve
`InvestigationActivities.Model` prompt'ları `StringBuilder.AppendLine` ile
kuruyor, yani modele giden metin Windows'ta CRLF, Linux'ta LF taşıyor. Production
container'ları Linux olduğu için davranış değişmiyor, ama aynı aday Windows'ta
~5 token farklı ölçülüyor. P2-1 CI'ı Linux'ta koşacağı için baseline Linux'a
göre kaydedildi.

### P1-1 — BUG-001

Kabul kriterleri `adp-phased-execution-plan.md` "BUG-001" bölümündedir ve
burada tekrar tanımlanmaz:

1. `ix_incidents_active_fingerprint` unique partial index olur; migration
   mevcut veride çakışma varsa açık hata verir, sessiz silme yapmaz.
2. Aynı fingerprint için eşzamanlı iki create tek incident + bir recurrence
   üretir; `KafkaConsumerService` "aynı mesaj tekrarı" ile "farklı mesaj yarışı
   kaybetti"yi ayırır.
3. Aktif incident yokken gelen `IsResolved=true` teslimat soruşturulmayan
   incident açmaz.

Doğrulama: unit + Testcontainers integration (`--with-integration`).

### P1-1 notları

- Kalan: `POST /v1/incidents` yolundaki gerçek eşzamanlı yarış hâlâ yakalanmamış
  `DbUpdateException` ile 500 döner (sessiz değil, veri kaybı yok). Kafka yolu
  D42 ile yeniden dener; HTTP yolu için ayrı bir adım gerekirse burada açılır.
- `verify-end-to-end.md` recipe'i D39'dan beri geçersiz bir Alertmanager
  payload'ı ve yanlış süreçte bir metrik gösteriyordu; düzeltildi.
- Sandbox notu: compose doğrulaması `docker.redpanda.com` erişilemediği ve image
  build'i egress proxy CA'sına güvenmediği için repo dışı bir override ile
  koşuldu; repo dosyaları değişmedi.

### P1-2 — "Resolved" anlamı

Karar `docs/DECISIONS.md`'ye yazılmadan kod yazılmaz. Hedef: soruşturmanın
bitmesi incident'ı kapatmaz; kapanış alert'in resolved teslimatıyla veya
operatörle olur; açık incident'a gelen recurrence yeni soruşturma açmaz.
Kabul: süren kesintinin tekrar eden alert'leri tek incident'ta toplanır;
`IncidentStatePolicy` testleri yeni geçişleri kapsar; API contract değişikliği
`change-a-serialized-type` recipe'iyle yapılır.

### P1-3 — BUG-002

D41'in seçenek 2'si: `ActivateAsync`, aktivasyon tenant'ı erişilebilir bir
`TenantWide` assignment'ı olmadan bırakacaksa reddeder. Kabul:
`adp-phased-execution-plan.md` "BUG-002" kapanış kanıtı (a) ve (b).

### P1-4 — Onay reddi ve süre aşımı

Red, soruşturmayı remediation olmadan sürdürür ve raporu yazar; süre aşımı da
raporu yazar ve timeline'a nedeni kaydeder. Kabul: workflow testi ret ve süre
aşımında `PersistReport` çağrıldığını kanıtlar.

### P1-5 — Reopen aktörü

Reopen, kimliği doğrulanmış principal'ı aktör olarak kaydeder. Kabul: timeline
ve audit sabit `"operator"` yerine çağıranı taşır.

### P2-1 / P2-2 — CI ve image

`eng/ci.sh`'yi PR'da çalıştıran bir workflow; integration job'ı Docker'lı
runner'da; `deploy/docker/Dockerfile` build'i ve zafiyet taraması. Kabul: PR'da
gate kırmızıysa merge edilemez; workflow yerel gate ile aynı adımları çalıştırır.

### P3 — Trace sürekliliği ve readiness Faz 5

Kök neden (Kafka zarfı ve/veya Temporal activity sınırında context
propagation) bulunur, regresyon testi eklenir. Faz 5 adımları
`production-readiness.md`'de tanımlıdır.

### P4 — Sağlayıcı lehçeleri

P4-1…P4-3 mevcut adapter'ları değiştirir, yeni adapter türü eklemez. P4-4 ve
P4-5 yeni adapter/resolver ekler ve ADP F3 kapsamıyla birlikte yürür.

### P5, P6

Her biri kendi plan dosyasını ve gerektiğinde `DECISIONS.md` kaydını gerektirir;
başlamadan önce kapsam burada genişletilir.
