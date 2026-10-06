> Durum: Planlandı — 2026-09-10. Hiçbir adım tamamlandı olarak işaretlenmemiştir.
> Kapsam: ADP-001–ADP-605 işlerinin ve ADP-003 evidence closure planının tek
> sıralı yürütme zincirine bağlanması.
> Kapsam dışı: Bu belge yeni gereksinim üretmez, mevcut iki rehberin yerine geçmez.

# ADP faz yürütme planı — adım adım sıra

## Bu belgenin işi

Üç kaynak var ve üçü farklı soruyu yanıtlıyor:

| Belge | Yanıtladığı soru | Bu planda rolü |
|---|---|---|
| [`product-adaptability-implementation-and-testing.md`](product-adaptability-implementation-and-testing.md) | Ne yapılacak, hangi aşamada, hangi kabul ile | Asıl kapsam (ADP-001–ADP-605) |
| [`adp-003-evidence-closure.md`](adp-003-evidence-closure.md) | ADP-003 satırlarının kanıtı nasıl üretilir | Faz 0'ın uygulama detayı |
| [`adp-003-integration-support-and-testing.md`](adp-003-integration-support-and-testing.md) | Bir entegrasyon hangi hükümle işaretlenir | Faz 0'ın kanıt formatı ve envanteri |

Bu belge yalnız **sıra, adım sınırı ve kapı** kurar. Bir adımın içeriği
değişirse kaynak belge güncellenir; burada yalnız sıra ve durum tutulur.

## ADP-003 kapanış planı ana rehberde neyi kapsıyor

Doğrudan kapattığı: **Aşama 0 / ADP-003** ve **ADP-005 kararı**.

| Ana rehber işi | Kapanış planındaki karşılığı | Sonuç |
|---|---|---|
| ADP-003 evidence matrisi | Yaklaşım 1–5 (test host + testler), 6 (matris güncelleme) | Tam kapatır |
| ADP-005 adapter-vs-config triage | Context paragrafı: `docs/DECISIONS.md` kaydı | Karar olarak kapatır |
| ADP-001 sözlük/ownership/scope | Yok | Kapsamaz — F0-9 |
| ADP-002 hedef/kapsam dışı/başarı ölçüsü | Yok | Kapsamaz — F0-9 |
| ADP-004 iki pilot selection record | "Assumptions": bloklu bırakır | Kapsamaz — F0-10, bloklu |

Dolaylı ama kritik olan: kapanış planı, ana rehberin Aşama 1–4 test matrislerinde
`HTTP integration`, `API + integration` ve `Security integration` diye geçen tüm
assertionların **bugün evi olmadığı** gerçeğini çözer. `tests/SreAiAgent.ApiTests`
bir ADP-003 detayı değil, sonraki dört fazın paylaşılan altyapısıdır:

| Ana rehber satırı | Aşama | Bu host olmadan kanıtlanamaz |
|---|---|---|
| Tenant A/B kaydı reddedilir | 1 | API + repository integration |
| JWT/body scope çelişkisi 401/403 | 2 | HTTP integration |
| İki scoped assignment doğru adapter çözer | 3 | Application + integration |
| Yetkili/yetkisiz API/CLI scope | 4 | HTTP integration |
| Cross-product credential çözülmez | 4 | API + integration |
| Endpoint policy metadata adresini reddeder | 4 | Security integration |

Bu yüzden F0-1…F0-5 sırası pazarlık konusu değildir: en önce host gelir.

Kapanış planında bir tutarsızlık var ve bu planda çözülüyor: Context bölümü
ADP-005 triage kaydını vaat ediyor, Yaklaşım 6 ise yalnız D37'yi (test suite
ayrımı) yazıyor. F0-8 iki ayrı karar üretir: **D37** test ayrımı, **D38** ADP-005
triage.

## Faz zinciri ve kapı kuralı

~~~mermaid
flowchart LR
 F0[F0 Asama 0 + ADP-003 kanit] --> F1[F1 scope/snapshot]
 F0 --> F2[F2 integration contract]
 F1 --> F3[F3 provider/budget]
 F2 --> F3 --> F4[F4 onboarding]
 F4 --> F5[F5 iki pilot] --> F6[F6 readiness]
~~~

Her adım için değişmez kural:

1. Adım girişinde mevcut kod okunur; adapter, control plane, snapshot ve test
   yeniden inşa edilmez.
2. Önce en küçük bağımlılığı kapalı implementasyon, sonra riskine uygun test.
3. Adım çıkışında **önce** komut çalıştırılır, **sonra** doküman/evidence yazılır.
   Ters sıra bu planın engellemek için var olduğu hatadır.
4. Kod veya process sınırı değiştiyse varsayılan gate zorunludur:
   `powershell -ExecutionPolicy Bypass -File eng/ci.ps1`; Docker gerektiren iş
   için `pwsh -File eng/ci.ps1 -WithIntegration`.
5. Bir adım kanıt üretmediyse durumu `bilinmiyor` kalır; `planlandı` → `uygulandı`
   → `doğrulandı` sırası atlanmaz.
6. Faz içindeki adımlar numarasıyla sıralıdır; yalnız "paralel" işaretli adımlar
   birbirinden bağımsız yürütülebilir.

Okunacak skill'ler adım bazında verilmiştir: paket/lockfile için `add-a-package`,
serileşen tip için `change-a-serialized-type`, telemetry için `add-telemetry`,
stack doğrulaması için `verify-end-to-end`, yeni tool için `add-a-tool`.

---

## Faz 0 — Aşama 0 kapanışı ve HTTP sınır kanıtı

Kapsam: ADP-001, ADP-002, ADP-003, ADP-004, ADP-005.
Giriş: yok. Çıkış: sürümlü destek matrisi + ADP-003 kanıtı + ADP-005 kararı;
ADP-004 gerçek target gelene kadar bloklu kalır ve bu açıkça yazılır.

### F0-1 — `tests/SreAiAgent.ApiTests` iskeleti

- `tests/SreAiAgent.ApiTests/SreAiAgent.ApiTests.csproj`: yalnız
  `ProjectReference` → `src/SreAiAgent.Api`, `PackageReference` →
  `Microsoft.AspNetCore.Mvc.Testing` (Version yok; `Directory.Packages.props:112`
  pinliyor). Geri kalan her şey `tests/Directory.Build.props`'tan miras alınır.
- `src/SreAiAgent.Infrastructure/SreAiAgent.Infrastructure.csproj:26` öncesine
  `<InternalsVisibleTo Include="SreAiAgent.ApiTests" />`. Gerekçe: `IMessageProducer`
  internal ve host'ta substitute edilecek.
- `SreAiAgent.slnx:14` öncesine proje satırı (ordinal sırada `ApiTests`,
  `Application.Tests`'ten önce gelir, 4 boşluk girinti).
- `tests/SreAiAgent.ApiTests/RepositoryPaths.cs`: `Infrastructure.Tests`
  kopyasının namespace'i değişmiş hâli. Ortaklaştırılmaz.
- Skill `add-a-package` okunur; `dotnet restore SreAiAgent.slnx --force-evaluate`.
- Kabul: `tests/SreAiAgent.ApiTests/packages.lock.json` oluşur; `eng/ci.*`
  dosyalarına dokunulmaz (Docker gerekmiyor, `Category=Integration` listesine
  eklenmez).

### F0-2 — Üretim JWT yolundan test tokenı

- `tests/SreAiAgent.ApiTests/TestTokens.cs`: `Issuer`, `Audience`, 32 baytlık
  `SymmetricSecurityKey`, `For(string tenant, params string[] roles)` →
  `JsonWebTokenHandler.CreateToken`, HmacSha256, 5 dakika.
- `DevelopmentAuthenticationHandler` kullanılmaz: sabit principal ne anonim
  çağrıyı ne yanlış rolü ne de tenant B'yi ifade edebilir.
- Kabul: derlenir; `Microsoft.IdentityModel.JsonWebTokens` transitive gelmezse
  kapanış planının "Assumptions" maddesi uygulanır (central package + yeniden
  `--force-evaluate`), lock dosyası elle düzenlenmez.

### F0-3 — Docker'sız host fixture

- `SreApiFactory : WebApplicationFactory<Program>`, `UseEnvironment("Testing")`.
- In-memory config override'ları: `Database:ConnectionString`,
  `Database:MigrateOnStartup=false`, `Registries:ConfigurationRoot`,
  `Registries:PromptRoot`, `Workflow:Engine=inline`, `Outbox:Enabled=false`,
  `Security:AllowDevelopmentAuthentication=false`, `Security:Audience`.
- `PostConfigure<JwtBearerOptions>`: discovery kapatılır, validation parametreleri
  `SecurityDependencyInjection.cs:42-67` ile birebir aynı tutulur.
- Dört servis değiştirilir: `IEventPublisher`, `IUnitOfWork`,
  `IModelProviderControlPlane`, `IMessageProducer`. İlk ikisi
  `PersistenceDependencyInjection.cs:109,119`'da `TryAddScoped` olduğu için
  `RemoveAll` şart.
- `TestDoubles.cs`: `RecordingEventPublisher` (iki overload), `CountingUnitOfWork`.
- `Reset()` + `IClassFixture<SreApiFactory>`: host bir kez ayağa kalkar.
- Kabul: F0-5'teki `HostBootTests` yeşil olana kadar bu adım kapanmaz.

### F0-4 — Alert ingress HTTP sınırı (7 test)

`AlertIngressHttpTests.cs`, payload şekli `AlertAdapterTests.cs`'ten alınır:
kabul/202+tek mesaj, tokensız/401, Viewer/403, çapraz tenant/403 `FORBIDDEN`,
tenant claim yok/403, bilinmeyen source/400, malformed payload/400. Her red
vakası ayrıca **kaydedicinin hiç dokunulmadığını** doğrular.

### F0-5 — Yönetim yüzeyi ve boot (4 test)

`ModelProviderAuthorizationTests.cs` (3): token tenantı control plane'e geçer,
Investigator 403, anonim 401 — üçünde de substitute çağrı sayısı doğrulanır.
`HostBootTests.cs` (1): `GET /health/live` hiçbir backend olmadan 200.
Kabul: F0-1…F0-5 toplamı **11 traitsiz test**; varsayılan gate sayısı 11 artar.

### F0-6 — Kalıcı yan etki kanıtı (Docker)

- `SreAiAgent.IntegrationTests.csproj`'a Api referansı + `Mvc.Testing`; restore
  F0-1 ile birlikte tek `--force-evaluate` koşusunda yapılır.
- `SreApiPostgresFactory`: yalnız `IMessageProducer` substitute; `IEventPublisher`,
  `IUnitOfWork`, `IModelProviderControlPlane` üretim kalır.
- `AlertIngressPersistenceTests` (2): kabul edilen alert tam bir outbox satırı
  bırakır; reddedilen çapraz tenant hiç satır bırakmaz. Sıra bağımsızlığı için
  temizlik zorunlu.
- `ModelProviderTenantHttpTests` (1): başka tenantın connection'ı HTTP'de
  **404 `PROVIDER_CONNECTION_NOT_FOUND`** (403 satırın varlığını doğrular).
- Her yeni sınıf `[Trait("Category", "Integration")]` taşır, yoksa
  `SuiteConventionTests` kırılır.

### F0-7 — Gate koşusu ve kanıt kaydı

1. `dotnet restore SreAiAgent.slnx --force-evaluate`
2. `powershell -ExecutionPolicy Bypass -File eng/ci.ps1` → exit 0, sıfır uyarı.
3. `dotnet test tests/SreAiAgent.ApiTests/SreAiAgent.ApiTests.csproj --no-build`
   → çapraz tenant 403 ve temiz akış 202 gözlemlenir. 403 yerine 202 görülürse
   tenant guard HTTP'de devrede değildir; bu suite tam bunun için var.
4. `pwsh -File eng/ci.ps1 -WithIntegration` → exit 0 + F0-6 assertionları.
5. ADP-003 release evidence şemasıyla (`adp-003-...md:192-204`) secret içermeyen
   kayıt yazılır: test adları, tarih, code revision, sonuç.

### F0-8 — Doküman ve karar güncellemesi

- `adp-003-integration-support-and-testing.md`: envanter satır 31 `Doğrulandı`
  sütunu; satır 103-108 tablosuna kanıtlayan test adları; satır 110-112'deki
  "mevcut in-memory publisher" ifadesi gerçekte eklenen iki double ile
  değiştirilir; satır 214'ten `eksik HTTP integration host veya fake` çıkarılır.
  Provider satırları `Bilinmiyor` kalır — canlı koşu yok.
- `product-adaptability-implementation-and-testing.md`: satır 103, 110 ve 134-135
  ADP-003 kanıtlı / ADP-004 bloklu olacak şekilde güncellenir.
- `docs/plans/README.md`: ADP-003 satırı ve bu plan satırı.
- `docs/DECISIONS.md`: **D37** iki suite ayrımı ve bedeli (iki fixture, kopya
  token yardımcısı); **D38** ADP-005 adapter-vs-config triage (ana rehber
  satır 120-125 tablosunun karar hâline getirilmesi).
- Tüm Türkçe belgelerde sütun sayısı ve satır genişliği korunur.

### F0-9 — ADP-001 ve ADP-002 (paralel)

Kapanış planının kapsamadığı iki doküman işi:
- ADP-001: product/tenant/environment sözlüğü, ownership, scope kararı. Bugünkü
  gerçek: `IModelProviderControlPlane`'in 13 üyesi yalnız `string tenant` alır;
  product/environment boyutu **hiçbir yerde yok**. Sözlük bu gerçeği başlangıç
  noktası olarak yazar, var gibi göstermez.
- ADP-002: hedef, kapsam dışı, başarı ölçüsü; README ve ARCHITECTURE ile
  çelişmeyecek şekilde.
Kabul: iki bölüm de ana rehberde referanslı; kod değişikliği yok.

### F0-10 — ADP-004 (BLOKLU)

İki gerçek product/environment target, alert source, güvenli datasource listesi,
owner, veri sınıfı, hacim, failure budget, rollback sahibi ve önceden ilan
edilmiş kalite eşiği gerekir. Ana rehber isim uydurmayı yasaklıyor.
**Kullanıcıdan girdi bekler.** Girdi gelene kadar Aşama 0 açık kalır ve Faz 1
yalnız ADP-004'e bağlı olmayan işleri kapsar.

---

## Faz 1 — product/environment control plane ve snapshot uyumluluğu

Kapsam: ADP-101–ADP-105. Giriş: F0-9 sözlüğü ve scope kararı.

| Adım | İş | Sınır ve kanıt |
|---|---|---|
| F1-1 | Scope modeli kararı (`docs/DECISIONS.md`): product/environment ayrı entity mi, tenant'a bileşik anahtar mı | Karar yazılmadan şema yazılmaz |
| F1-2 | Product/Environment: composite key sütunları (entity değil, D40), `TenantScope`, EF configuration, migration (ADP-101) | Testcontainers round-trip; önceki veri korunur; illegal kombinasyon `TenantScope` construction'ında reddedilir |
| F1-3 | Scope policy: principal → product/env; create/read/update/activate doğrulaması (ADP-101) | `docs/DECISIONS.md` D40'ın yetki matrisini kanıtlar, **F0-3 host'unda**: çapraz tenant reddedilir; aynı tenant farklı product reddedilir; claim'siz çağıran (`TenantWide`) başka bir product'ın satırını okuyamaz; `product` olmadan `environment` claim'i `TenantScope` construction'ında reddedilir (repository'ye hiç ulaşmaz) |
| F1-4 | Credential reference yaşam döngüsü (ADP-102) | Raw secret API/DB/snapshot/log/audit'e girmez; security unit |
| F1-5 | Scoped model assignment: `IModelProviderControlPlane` 13 üyesinin cutover'ı (ADP-103) | Tüm çağıranlar taşınır; shim veya overload bırakılmaz |
| F1-6 | Versioned immutable snapshot alanları (ADP-104) | Skill `change-a-serialized-type`; beş JSON context'ten hangileri değişiyor açıkça listelenir |
| F1-7 | Geriye uyum: eski snapshot ve `WorkflowPayloadJson` deserialize (ADP-105) | Eski payload yeni active assignment'a **kaymaz** |
| F1-8 | Gate + evidence + doküman | `ci.ps1` ve `-WithIntegration`; Aşama 1 satırları güncellenir |

Çıkış: scope şeması, version/migration planı, API/CLI sözleşmesi, compatibility
evidence. UI zorunlu değil.

---

## Faz 2 — ortak integration contract ve güvenli normalizasyon

Kapsam: ADP-201–ADP-206. Giriş: F0 triage (D38); kalıcı scope gerekiyorsa F1.

| Adım | İş | Sınır ve kanıt |
|---|---|---|
| F2-1 | Ortak contract/capability descriptor (ADP-201) | Product kodu Application'a taşınmaz |
| F2-2 | Metric label ve log namespace mapping, scoped+versioned konfigürasyon (ADP-202) | Allowlist dışı query yok; `PromQuery.cs:75,84,161` sınırı korunur |
| F2-3 | Ingress auth + product/env normalizasyonu (ADP-203) | Scope body'den gelmez; F0-4 testleri genişletilir, yeniden yazılmaz |
| F2-4 | Süre/seri/payload/concurrency limitleri (ADP-204) | Gateway keser; prompt metni mekanizma değil |
| F2-5 | No-data / unavailable / partial / conflict semantiği (ADP-205) | Boş evidence başarı sayılmaz |
| F2-6 | Gerekirse adapter extension (ADP-206) | Fake production adapter yok; gerekmezse adım "gerekmedi" diye kapanır |
| F2-7 | Datasource başına deterministic contract evidence + doküman | `ContractTests` Docker gate'i |

---

## Faz 3 — provider seçimi, capability, budget ve rotation

Kapsam: ADP-301–ADP-306. Giriş: F1 scope/snapshot, F2 contract, iki gerçek
provider target'ı (bloklu bileşen).

| Adım | İş | Sınır ve kanıt |
|---|---|---|
| F3-1 | Product/env/agent bazlı provider-model seçimi (ADP-301) | Pinned revision ile çözülür; parametre düşürme veya scripted'a dönüş yasak; ADP-004 canlı çalıştırması tek-sağlayıcı riskini gösterdi (product-adaptability-implementation-and-testing.md, "Çoklu sağlayıcı gereksinimi") |
| F3-2 | Capability activation validation: descriptor bazlı (ADP-302) | Model adından yetenek tahmini yok; eksik slot atomik reddedilir; `OpenAiCompatible` bugün yalnızca `StructuredOutput \| SystemMessage` verir, `analytical` profilinin tool-calling talebiyle çakışır (aynı bulgu) |
| F3-3 | Açık fallback / disabled davranışı (ADP-303) | Cloud fallback varsayılan kapalı; açıksa policy + allowlist + aggregate budget |
| F3-4 | Gateway-enforced budget (ADP-304) | Budget dolduğunda outbound çağrı yok; audit/metric güvenli |
| F3-5 | Credential rotation ve revision cutover (ADP-305) | Yeni revision üretir; eski snapshot çözülemezse açık hata |
| F3-6 | İki gerçek provider için opt-in live kanıt (ADP-306) | **BLOKLU**: credential + onaylı target gerekir; eksikse `failed`/`blocked`, asla skip; ADP-004'ün OpenAI 429 kesintisi ikinci gerçek provider ihtiyacını doğruladı (aynı bulgu) |
| F3-7 | Planlı fixture'lar: local compatible endpoint contract, native Gemini wire, discovery'siz manual model ID | Bunlar live kanıt değildir, öyle yazılmaz |
| F3-8 | Gate + evidence + doküman | Adapter dosyasının varlığı kanıt sayılmaz |

---

## Faz 4 — API/CLI-first onboarding ve operasyon akışı

Kapsam: ADP-401–ADP-406. Giriş: F1–F3 ve iki örnek için gerçek karar.

| Adım | İş | Sınır ve kanıt |
|---|---|---|
| F4-1 | Scoped product/env config API/CLI (ADP-401) | Yetkili/yetkisiz akış F0 host'unda; output secret içermez |
| F4-2 | Secret içermeyen connectivity/capability diagnostics (ADP-402) | DNS/200 yetmez: auth scope, allowlisted query, no-data/partial ayrımı |
| F4-3 | Test-alert → evidence → report draft (ADP-403) | Skill `verify-end-to-end`; compose stack |
| F4-4 | Evaluate/activate + audit/evidence (ADP-404) | Verify/eval/capability olmadan pointer değişmez |
| F4-5 | İki product onboarding runbook örneği (ADP-405) | ADP-004 target'larına bağlı — bloklu |
| F4-6 | Immutable release rollback ve operatör teşhisi (ADP-406) | Önceki release'e dönüş kanıtlı |
| F4-7 | Endpoint trust policy: onaylı local kabul, metadata/service adresi reddi | Security integration; SSRF sınırı |
| F4-8 | Gate + evidence + doküman | API/CLI reference yayımlanır |

---

## Faz 5 — iki gerçek pilotla kalite ve güvenilirlik

Kapsam: ADP-501–ADP-505. Giriş: F4 + iki gerçek target + F0-10 eşikleri.
Bu faz `production-readiness.md`'deki eski Phase 5 **değildir**.

| Adım | İş | Bloklu mu |
|---|---|---|
| F5-1 | RCA quality rubric ve evidence grounding (deterministic eval kısmı) | Hayır — gold fixture ile kurulabilir |
| F5-2 | Evidence reliability, erişim, citation doğruluğu | Hayır (deterministic), pilot ölçümü evet |
| F5-3 | Missing/conflicting data ve uncertainty kalibrasyonu | Hayır |
| F5-4 | Partial outage, latency, cost/budget ölçümü | Kısmen — failure injection hayır, p95/maliyet evet |
| F5-5 | Hallucination, prompt injection, leak negatif testleri | Hayır |
| F5-6 | Pilot başına immutable candidate tuple + threshold karşılaştırması | **Evet** — gerçek target ve eşik şart |

Eşik geçilmezse sonuç `failed`/`blocked` kalır; bir pilot diğerine genellenmez.

---

## Faz 6 — readiness, recovery ve işletim dayanıklılığı

Kapsam: ADP-601–ADP-605. Giriş: F5 pilot evidence **ve** eski readiness Phase 5
kanıtı (`production-readiness.md`: Phase 5 bugün pending;
remediation planının ayrıntılı adım metni hiç commit edilmedi; kayıt `production-readiness.md`'dir).

| Adım | İş | Kanıt katmanı |
|---|---|---|
| F6-1 | Eski readiness Phase 5 tekrar kontrolü; replay/duplicate, load, noisy neighbor, budget izolasyonu (ADP-601) | Integration/stack, load |
| F6-2 | PostgreSQL, Kafka, Temporal recovery ve migration/restore (ADP-602) | Recovery/stack; skill `verify-end-to-end` |
| F6-3 | Kubernetes CNI, secret delivery, network policy (ADP-603) | K8s opt-in |
| F6-4 | Safe product/env tracing, metrics, logs, correlation (ADP-604) | Skill `add-telemetry`; allowlist dışı etiket düşer, raw query/prompt/secret etiket olmaz |
| F6-5 | Support matrix, runbook, ownership/on-call handoff (ADP-605) | Doküman + sahiplik |

Kanıt yalnız ölçülen product/env/revision için geçerlidir.

---

## Bilinen defekt — mutlaka düzeltilmeli, ayrı bir faz değil

> **Durum (2026-10-05): kapandı.** Üç kabul kriteri `project-assessment-2026-10.md`
> P1-1 kapsamında uygulandı ve doğrulandı; kararlar `DECISIONS.md` D42 (consumer)
> ve D43 (alert kimliği ve cooldown). Kapanış sırasında ikinci mekanizmanın
> dayandığı yeniden teslimin de çalışmadığı bulundu: başarısız mesaj seek
> edilmediği için aynı partition'daki sonraki başarılı commit onu atlıyordu (D42).

**BUG-001 — Bir incident iki bağımsız doğrulanmış yoldan kalıcı olarak soruşturulmadan kalabiliyor: "resolved" durumundaki bir teslimatın açtığı ölü bir incident, ve daha geniş bir catch bloğunun sessizce attığı gerçek bir eşzamanlı yarış.**

**Birinci mekanizma — hipotez değil, pilot'un kendi veritabanı sorgulanarak
doğrulandı.** `postgres-data` volume'ünden doğrudan okundu (`incidents`,
`incident_events`, `outbox_messages`, 2026-09-12 verisi). İlk rapor "aynı
fingerprint için aynı anda iki incident satırı" diyordu; bu **yanlıştı** ve
burada geri çekiliyor — ama altında farklı, gerçek bir kusur çıktı.

`978abfd0ab45b6c0` fingerprint'i için `incidents` tablosunda altı satır var,
hepsi sırayla açılıp `Resolved` ile kapanmış, hiçbiri çakışmıyor. İlk ikisi:
- `01a095fd…`: `created_at` 14:20:46.27, `resolved_at` 14:20:48.25 (2 saniye
  sonra) — investigation başladı, üç expert de (database/app/kubernetes) o
  zamanki flag/classification nedeniyle elendi, `InsufficientAgentCapability`
  ile otomatik kapandı.
- `01a09602…`: `created_at` 14:25:46.11 — **birincisinden 5 dakika sonra,
  birincisi zaten `Resolved` olduktan sonra**. `configuration_snapshot_id`
  **NULL**; timeline'ında yalnız `sre.incident.created` ve üç
  `sre.incident.updated` ("Alert count is now 2/3/4") var, hiç "Investigation
  started" yok.

İki satırın da `detected_at`'i aynı (`2026-09-12 14:20:30.948+00`), ve
`outbox_messages`'taki her iki `sre.incident.created` olayının `occurred_at`'i
de bu değer — çünkü `IncidentOpened.OccurredAt`, `DetectedAt`'ten geliyor
(`IncidentEvents.cs:19`), gerçek yaratılma anından değil. Raporun "aynı anda
oluşturuldu" izlenimi muhtemelen buradan geldi. Gerçek yaratılma zamanları
(`created_at`) 5 dakika ayrı ve sıralı: **bu bir yarış değil.**

İkinci satırın hiç soruşturulmamasının nedeni doğrulandı: `AlertmanagerAlertAdapter`
her alarmın kendi `status` alanını okuyor (`AlertSourceAdapters.cs:104-105`)
ve `AlertNormalizer.cs:89`, `StartInvestigation = !alert.IsResolved`
uyguluyor. Birincisi kapandıktan sonra bu fingerprint için aktif hiçbir
incident yokken gelen `"resolved"` durumundaki bir Alertmanager teslimatı,
`CreateIncidentHandler.OpenAsync`'te `startInvestigation = false` ile
**yeni** bir incident açıyor — investigation hiç başlamıyor
(`ConfigurationSnapshotId` yalnız `StartInvestigation(...)` çağrısında set
ediliyor, `Incident.cs:169`; constructor set etmiyor). Bu andan sonra
fingerprint tekrar "aktif" sayıldığı için, o gece gelen üç gerçek
`sre.alert.received` teslimatı (14:33, 14:39, 14:44 — SMTP kesintisi gerçekten
sürüyor) `RecordRecurrenceAsync`'e düşüyor; bu metod `incident.Status`'a hiç
bakmıyor, yalnızca `AlertCount`'u artırıp gerekirse severity yükseltiyor —
**asla** `StartInvestigation` çağırmıyor. Sonuç: gerçek, sürmekte olan bir SMTP
kesintisi üç kez daha bildiriliyor ve üçünde de hiçbir soruşturma üretilmiyor.

**İkinci mekanizma — kod okumasıyla doğrulandı, bu pilotun neye rastladığından
bağımsız.** `ix_incidents_active_fingerprint` (`IncidentConfiguration.cs:51-53`)
filtrelenmiş düz bir btree indeks, `IsUnique()` çağrısı yok. Tek gerçek koruma
`IdempotencyStore.TryClaimAsync`'in `idempotency_keys.Key` birincil anahtarı
(`HasKey(key => key.Key)`, `SupportingStores.cs:71-124`) ve
`CreateIncidentHandler.OpenAsync`'in bunu incident/snapshot/timeline/outbox
satırlarıyla tek `SaveChangesAsync`'te commit etmesi — gerçekten eşzamanlı bir
yarışta kaybedenin **bütün** transaction'ı rollback olur, yetim bir incident
satırı kalmaz. Kusur bir seviye yukarıda: `KafkaConsumerService.ProcessAsync`'in
`catch (DbUpdateException) when (IsDuplicateDelivery(...))` bloğu
(`:228-238,274-275`) **her** unique violation'ı "bu mesaj zaten işlendi" sayıp
offset'i sessizce commit ediyor — inbox satırının kendi `EventId`'i
çakıştığında bu doğru, ama idempotency-key çakışması **farklı** bir mesajın
(farklı `EventId`, aynı fingerprint) yarışı kaybettiği anlamına geliyor ve bu
durumda kod, alarmı `RecordRecurrenceAsync`'e yönlendirmek yerine tamamen
atıyor: ne timeline kaydı, ne `AlertCount` artışı, ne severity escalation.
Doğrudan HTTP yolunda (`IncidentEndpoints.cs` `CreateAsync`) bu race'i
yakalayan hiçbir catch yok; eşzamanlı bir POST bu yarışı kaybederse
`DbUpdateException` yakalanmadan fırlar (kayıtsız 500, en azından sessiz
değil).

**Etki, her iki mekanizma için doğrulandı — sınıf: veri kaybı.** Gerçek,
süregelen bir alarm — pilotta üç kez, teorik yarışta bir kez — hiçbir
soruşturma, hiçbir bulgu, hiçbir operatör sinyali üretmeden kayboluyor: ne
timeline'da, ne raporda, ne metrikte hiçbir iz kalmıyor.

**Kabul kriterleri, üçü de gerekli:**
1. `ix_incidents_active_fingerprint` gerçek bir `IsUnique()` partial index
   olur: `(tenant, source, fingerprint) WHERE status NOT IN ('Resolved',
   'Cancelled')`.
2. Eşzamanlılık testi: aynı fingerprint için gerçekten eşzamanlı iki create
   çağrısı **tek bir incident ve tek bir kaydedilmiş recurrence** üretir,
   sessiz bir discard değil — bu, `KafkaConsumerService.IsDuplicateDelivery`'nin
   "aynı mesaj tekrarı" ile "farklı mesaj, fingerprint yarışını kaybetti"
   durumlarını ayırt etmesini ve ikincisini `RecordRecurrenceAsync`'e
   yönlendirmesini gerektirir.
3. "Resolved" durumundaki teslimat testi: aktif incident yokken gelen bir
   `IsResolved=true` teslimat yeni, soruşturulmamış bir incident açmıyor — ya
   da açtıysa, sonraki bir `IsResolved=false` teslimat geldiğinde
   `RecordRecurrenceAsync`, sayaç artırmak yerine `StartInvestigation`'ı
   tetikliyor.

**Yerleşim:** kendi başına, bağımsız bir kayıt — F1-2'nin veya başka herhangi
bir adımın kapsamına sessizce eklenmez. Incident fingerprint tekilliği ve
alarm-yaratma yolu, product/environment şema işinden farklı bir sınırlı
bağlamdır; ikisini birleştirmek ya o migration'ın review'ına ilgisiz risk
sızdırır ya da F1-2 kapsamı daraltıldığında unutulur. Sahibin tercihi: F1
adımları arasına güvenli bir kesim olarak (kendi adımı, mevcut bir adıma
eklenti değil) girsin, ya da fazlardan sonra ayrı bir iş olarak yapılsın.
**Burada uygulanmaz.**

---

> **Durum (2026-10-05): kapandı.** Seçenek 2 uygulandı (`DECISIONS.md` D44);
> kapanış kanıtı (a) ve (b) `ModelControlPlaneTests` içinde. Silme/geri çekme
> işlemi olmadığı için (b)'nin "geri çekilme" hali yerine "hiç TenantWide yokken
> dar aktivasyon reddedilir" kanıtlandı.

**BUG-002 — Bir operatör `TenantWide` seviyesinde aktif bir assignment bırakmadan bir `ProductWide` veya `Full` release'i aktive ederse, o tenant'ın **bütün** incident'ları sessizce soruşturulamaz hale geliyor; ne şema ne de `ActivateAsync`'in başarılı yanıtı bunu uyarıyor.**

**Kaynak — D41 (`DECISIONS.md`), kod okumasıyla doğrulandı, hipotez değil.**
D41, `GetActiveSnapshotAsync`'in tam kapsam eşleşmesi (exact match) kullanmasına,
`Full → ProductWide → TenantWide` gibi bir fallback zincirine **asla**
başvurmamasına karar verdi — çünkü bu metodun tek gerçek çağrı yolu
(`ConfigurationPinner.PinCurrentAsync`, `CreateIncidentHandler`/
`StartInvestigationHandler`/`RetryInvestigationHandler` ve
`ModelAssignmentBootstrapper` üzerinden) principal'siz ve yalnızca çıplak bir
tenant string'ine sahip: `Incident`'ın şemasında `Product` alanı yok,
`LlmOptions.BootstrapOptions`'ta da yok. Bu karar doğruydu — kanıt onu
destekliyor — ama kendi maliyet paragrafı şunu açıkça söylüyor: `TenantWide`
dışına daraltan bir operatör, daraltılmış ürüne değil, **o tenant'ın bütün
incident'larına** ait yapılandırma çözümlemesini kırıyor, çünkü hiçbir
etiketlenmemiş incident hangi ürüne ait olduğunu seçemiyor.

**Etki, sınıf: sessiz operasyonel kesinti.** `ActivateAsync`
(`IModelProviderControlPlane`) tenant'ın hâlâ erişilebilir bir `TenantWide`
assignment'ı olup olmadığını kontrol etmiyor; başarılı `ModelAssignmentRelease`
döndürüyor, ne bir uyarı ne bir hata. Bundan sonra o tenant için açılan her
incident, `ConfigurationPinner.PinCurrentAsync` içinde
`ApplicationErrorCodes.ModelAssignmentNotFound` ile başarısız olur —
soruşturma hiç başlamaz — operatör bunu fark edene ve elle bir `TenantWide`
release'i yeniden aktive edene kadar. Şemada bu durumu engelleyen hiçbir
kısıt yok; D41 bunu bilerek kabul etti, çünkü kapsamındaki tek gerçek soru
"fallback var mı yok mu" idi, "aktivasyon operatörü uyarmalı mı" değil.

**Kabul kriterleri, D41'in kendi metninde adı geçen iki seçenekten biri —
hangisi seçilirse kapanır:**
1. Incident pipeline'ı bir ürün boyutu kazanır (ör. `Incident.Product`), böylece
   `ProductWide`/`Full` seviyeleri `GetActiveSnapshotAsync`'in tek gerçek çağrı
   yolundan gerçekten çözümlenebilir hale gelir; **veya**
2. `ActivateAsync`, bir `ProductWide`/`Full` release'i aktive etmek tenant'ı
   erişilebilir hiçbir `TenantWide` assignment'ı olmadan bırakacaksa reddeder
   ya da açıkça uyarır — bu, `ModelProviderRepository`'nin aktivasyon anında
   aynı tenant için `TenantWide` kapsamlı bir aktif assignment sorgulamasını
   gerektirir.

Hangisi seçilirse seçilsin, kapanış kanıtı: (a) bir entegrasyon testi, bir
`TenantWide` release aktifken bir `ProductWide` release'in aktive edilmesinin
ardından o tenant için yeni bir incident'ın soruşturmaya başladığını
doğrulamalı, ve (b) seçenek 2 ise, aynı senaryoda `TenantWide` release'in
geri çekilmesinin `ActivateAsync`'i reddettiğini veya uyardığını doğrulayan
ayrı bir test.

**Yerleşim:** BUG-001 ile aynı gerekçeyle kendi başına, bağımsız bir kayıt —
F1-3'ün veya F1-5'in kapsamına sessizce eklenmez; her ikisi de bu tuzağı
*açıklıyor*, kapatmıyor. Ürün boyutunun nereye ekleneceği (incident şeması mı,
aktivasyon kontrolü mü) ayrı bir tasarım kararı gerektiriyor ve F1 fazının
geri kalanının review'ına ilgisiz risk sızdırmamalı. **Burada uygulanmaz.**

---

## Kullanıcıdan beklenen girdiler

| Girdi | Bloklanan adım | Neden uydurulamaz |
|---|---|---|
| İki gerçek product/environment target ve eşikleri | F0-10, F4-5, F5-6 | Ana rehber isim uydurmayı yasaklıyor |
| İki gerçek provider için credential ve onaylı endpoint | F3-6 | Eksik credential skip değil failure sayılır |
| Kubernetes cluster erişimi | F6-3 | Opt-in altyapı kanıtı |

Bu girdiler gelmeden F0-1…F0-9, F1, F2, F3-1…F3-5, F3-7, F4-1…F4-4, F4-6, F4-7,
F5-1…F5-5 ve F6-1, F6-2, F6-4 yürütülebilir.

## İlerleme tablosu

Durum yalnız şu değerleri alır: `planlandı`, `uygulandı`, `doğrulandı`, `bloklu`.

| Adım | Durum | Kanıt |
|---|---|---|
| F0-1 … F0-7 | doğrulandı | SreAiAgent.ApiTests (11 test), IntegrationTests (30 test); adp-003-evidence-closure.md |
| F0-8 | uygulandı | adp-003-integration-support-and-testing.md, product-adaptability-implementation-and-testing.md, DECISIONS.md D37/D38, README.md |
| F0-9 | uygulandı | product-adaptability-implementation-and-testing.md (ADP-001 tekil tenant sözlüğü ve scope kararı, ADP-002 hedef/kapsam dışı) |
| F0-10 | bloklu | Pilot 2 adayı kaydedildi (TicketHub); Pilot 1 ve kalite eşiği bekleniyor |
| F1-1 | uygulandı | DECISIONS.md D40 (scope modeli), D41 (exact-match çözümleme) |
| F1-2 | uygulandı | `fe67c2a` TenantScope sütunları + migration |
| F1-3, F1-5 | uygulandı | `43056cd` TenantScope cutover; `60589f2` API scope testi |
| F1-4 | uygulandı | `8087005` credential reference yaşam döngüsü; `773fcf4` sızıntı testi |
| F1-6 … F1-8 | planlandı | — |
| F2-1 … F2-7 | planlandı | — |
| F3-1 … F3-5, F3-7, F3-8 | planlandı | — |
| F3-6 | bloklu | Credential/target yok |
| F4-1 … F4-4, F4-6 … F4-8 | planlandı | — |
| F4-5 | bloklu | ADP-004 target'ları yok |
| F5-1 … F5-5 | planlandı | — |
| F5-6 | bloklu | Pilot target ve eşik yok |
| F6-1 … F6-2, F6-4 … F6-5 | planlandı | — |
| F6-3 | bloklu | Cluster erişimi yok |

Bir satır ancak komut çalıştırıldıktan ve kanıt referansı yazıldıktan sonra
güncellenir.
