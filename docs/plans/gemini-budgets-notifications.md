> **Durum:** tamamlandı — 2026-10-05. G-1'in canlı kanıtı credential bekliyor.
> **Kaynak:** `project-assessment-2026-10.md` P4-4, P5-1, P5-2; kullanıcı kararları
> (native Gemini adapter evet; bildirim kanalları Slack ve Teams; bütçe aylık
> maliyet veya belirlenmiş bir toplam maliyet limiti).
> **Kapsam dışı:** P4-5 secret store (karar bekliyor), P5-3 incident listeleme.

# Native Gemini, kiracı bütçesi ve Slack/Teams bildirimleri

## İlerleme

| Adım | İş | Durum | Kanıt |
|---|---|---|---|
| G-1 | `ProviderAdapterKind.Gemini`: adapter, wire contract, endpoint policy, verifier, API eşlemesi | uygulandı | D46; `ProviderAdapterTests` (5 sözleşme testi + Gemini gövde/token/sürüm), `ProviderEndpointPolicyTests`. Canlı kanıt credential bekliyor |
| B-1 | Kiracı bütçe politikası: şema, repository, API (`GET/PUT /v1/budget`) | doğrulandı | D47; `tenant_budgets` + migration, `GET/PUT/DELETE /v1/budget`; Postgres integration (aylık/fixed dönem). Compose: `GET` limit, dönem başı ve harcamayı gösteriyor |
| B-2 | Gateway'de zorlama: limit dolunca dışarı çağrı yok, `BUDGET_EXCEEDED` | doğrulandı | `LlmGateway` sağlayıcıya gitmeden reddeder (birim testi: resolver hiç çağrılmıyor, audit 0 maliyet). Compose: başarılı çağrı sayısı değişmedi, yeni satırlar `BUDGET_EXCEEDED` / 0 maliyet. İlk koşu ajan sonucunu `Failed`, raporu `Partial` gösterdi; executor artık bütçe reddini `BudgetExceeded` sayıyor (`AgentExecutorTests`), compose'da rapor `BudgetExceeded` |
| B-3 | Para birimi tutarlılığı: bütçe para birimi ile aktif deployment'ların para birimi | doğrulandı | Bütçe kurulurken ve aktivasyonda reddedilir (integration). Compose: USD release'e EUR bütçe 409 `BUDGET_CURRENCY_MISMATCH` |
| N-1 | Bildirim kanalları: şema, repository, API; webhook adresi `credential_ref` olarak | doğrulandı | D48; `notification_channels` + migration, `GET/POST/DELETE /v1/notification-channels`, `POST …/{id}/test`. Compose: ham URL 400, isim çakışması 409, test mesajı alıcıya ulaştı |
| N-2 | Slack ve Teams gönderim adapter'ları, endpoint policy | doğrulandı | `NotificationTests`: Slack `blocks` ve Teams Adaptive Card gövdeleri, mrkdwn kaçışı, production host/HTTPS/IP kuralları |
| N-3 | Olay tüketicisi: rapor tamamlandı, soruşturma başarısız, bütçe eşiği | doğrulandı | `sre.incident.completed.v1` hiç yazılmıyordu; activities artık outbox'a yazıyor (`InvestigationCompletionTests`). `NotificationDeliveryTests` (Postgres): kanal başına tek teslim, başarısız gönderimde talep bırakılıyor. Compose: Slack tamamlanmayı özetle aldı; yalnız failure/budget'a abone Teams almadı; %80 ve %100 eşikleri iki kanala da birer kez ulaştı |
| V | Gate (`--with-integration`) + compose doğrulaması + doküman | doğrulandı | Gate `--with-integration` yeşil; RUNBOOK kanal kurulumu ve arıza girdileri, retention'a talep kayıtları (62 gün) |

## G-1 — Native Gemini

OpenAI-uyumlu uç (D45) çalışıyor ama Gemini'nin kendi API'si yapılandırılmış
çıktıyı (`responseJsonSchema`), sistem talimatını ve kullanım sayaçlarını
(`thoughtsTokenCount` dahil) doğrudan verir; native adapter bunları kayıpsız
taşır.

- Wire: `POST {endpoint}/{apiVersion}/models/{model}:generateContent`,
  `x-goog-api-key` başlığı; `systemInstruction`, `contents`,
  `generationConfig { temperature, maxOutputTokens, responseMimeType,
  responseJsonSchema, seed }`.
- Kullanım: girdi `promptTokenCount`; çıktı `candidatesTokenCount +
  thoughtsTokenCount` (düşünme token'ları çıktı olarak faturalanır).
- `finishReason: MAX_TOKENS` gateway'in truncation kuralına düşer.
- Production host allowlist'i: `generativelanguage.googleapis.com`.
- Doğrulama: `GET {endpoint}/{apiVersion}/models`.
- Kabul: adapter contract testleri (başarı, durum kodu taksonomisi, boş içerik,
  credential sızmaması, gövde alanları). Canlı kanıt credential gerektirir.

## B — Kiracı bütçesi

Politika kiracı başına tek kayıttır: `limit`, `currency`, `period`.

- `period = monthly`: limit her UTC takvim ayında sıfırlanır.
- `period = fixed`: limit `starts_at`'tan itibaren toplam harcamaya uygulanır,
  sıfırlanmaz (belirlenmiş bir tutar).
- Harcama `llm_calls.estimated_cost` toplamıdır, başarısız çağrılar dahil
  (sağlayıcı onlar için de ücret alır). `(tenant, created_at)` indeksi eklenir.
- Gateway her model çağrısından önce harcamayı okur; limit dolmuşsa çağrı
  yapılmaz ve `BUDGET_EXCEEDED` döner. Bir çağrı limiti aşabilir (çağrı başına
  en fazla bir yanıtın maliyeti); bu kabul edilen maliyettir.
- Politika yoksa sınır yoktur (bugünkü davranış).
- Para birimi: bütçe varken aktive edilen release'in deployment'ları bütçeyle
  aynı para biriminde olmalıdır; bütçe kurulurken aktif release farklıysa
  reddedilir.
- Kabul: Postgres integration — limit altındayken çağrı yapılır, limit dolunca
  sağlayıcıya hiç istek gitmez; aylık dönem ay başında sıfırlanır; fixed dönem
  sıfırlanmaz.

## N — Slack ve Teams bildirimleri

Kanal kiracıya aittir: `kind` (`slack` | `teams`), `display_name`,
`webhook_ref` (`env://` veya `file://`; ham URL API'de, veritabanında, logda
yer almaz — webhook URL'si bir sırdır), `events`, `enabled`.

- Olaylar: `investigation_completed` (sonuç + özet), `investigation_failed`,
  `budget_threshold` (%80 ve %100).
- Gönderim: Slack incoming webhook (`text` + `blocks`), Teams Workflows webhook
  (Adaptive Card ekli `message`). Gövde yalnızca platformun kendi alanlarından
  kurulur; sağlayıcıdan gelen ham metin bildirime girmez.
- Endpoint policy: production'da Slack için `hooks.slack.com`, Teams için
  `*.webhook.office.com`, `*.logic.azure.com`,
  `*.environment.api.powerplatform.com`; HTTPS zorunlu, IP adresi yok.
- Tetik: olay topic'i üzerinden kendi inbox'ı olan bir tüketici; gönderim
  başarısızlığı D42'nin yeniden deneme ve dead-letter yolunu izler.
- Kabul: adapter gövde testleri; tüketici testleri; compose'da yerel bir
  webhook alıcısına gerçek gönderim.
