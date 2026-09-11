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
| F1-2 | Product/Environment entity, EF configuration, migration (ADP-101) | Testcontainers round-trip; önceki veri korunur |
| F1-3 | Scope policy: principal → product/env; create/read/update/activate doğrulaması (ADP-101) | Tenant A/B ve aynı tenant farklı product testleri **F0-3 host'unda** |
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
| F3-1 | Product/env/agent bazlı provider-model seçimi (ADP-301) | Pinned revision ile çözülür; parametre düşürme veya scripted'a dönüş yasak |
| F3-2 | Capability activation validation: descriptor bazlı (ADP-302) | Model adından yetenek tahmini yok; eksik slot atomik reddedilir |
| F3-3 | Açık fallback / disabled davranışı (ADP-303) | Cloud fallback varsayılan kapalı; açıksa policy + allowlist + aggregate budget |
| F3-4 | Gateway-enforced budget (ADP-304) | Budget dolduğunda outbound çağrı yok; audit/metric güvenli |
| F3-5 | Credential rotation ve revision cutover (ADP-305) | Yeni revision üretir; eski snapshot çözülemezse açık hata |
| F3-6 | İki gerçek provider için opt-in live kanıt (ADP-306) | **BLOKLU**: credential + onaylı target gerekir; eksikse `failed`/`blocked`, asla skip |
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
`../../PRODUCTION_READINESS_PLAN.md` remediation planı).

| Adım | İş | Kanıt katmanı |
|---|---|---|
| F6-1 | Eski readiness Phase 5 tekrar kontrolü; replay/duplicate, load, noisy neighbor, budget izolasyonu (ADP-601) | Integration/stack, load |
| F6-2 | PostgreSQL, Kafka, Temporal recovery ve migration/restore (ADP-602) | Recovery/stack; skill `verify-end-to-end` |
| F6-3 | Kubernetes CNI, secret delivery, network policy (ADP-603) | K8s opt-in |
| F6-4 | Safe product/env tracing, metrics, logs, correlation (ADP-604) | Skill `add-telemetry`; allowlist dışı etiket düşer, raw query/prompt/secret etiket olmaz |
| F6-5 | Support matrix, runbook, ownership/on-call handoff (ADP-605) | Doküman + sahiplik |

Kanıt yalnız ölçülen product/env/revision için geçerlidir.

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
| F1-1 … F1-8 | planlandı | — |
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
