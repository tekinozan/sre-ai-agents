> **Durum:** planlandı — 2026-10-05.
> **Kaynak:** `project-assessment-2026-10.md` P5-3, P6-1, P6-2, P6-3; kullanıcı
> kararı: bu dört madde sırayla, secret store (P4-5) şimdilik ertelendi.

# Incident listeleme, RCA geri bildirimi ve eval promotion kapısı

## İlerleme

| Adım | İş | Durum | Kanıt |
|---|---|---|---|
| L | `GET /v1/incidents`: filtreler, keyset sayfalama, son rapor sonucu | uygulandı | `IncidentListingTests` (Postgres, HTTP): 7 incident 3 sayfada eksiksiz ve tekrarsız, eşit `detected_at` id ile ayrılıyor, başka kiracı görünmüyor; her filtre; son raporun sonucu; 7 bozuk girdi 400. Compose doğrulaması V'de |
| F | RCA geri bildirimi: rapor sürümüne operatör hükmü, liste, metrik, eval'e dönüş prosedürü | planlandı | — |
| E-1 | Eval tamamlamada görev ayrılığı ve artifact doğrulaması | planlandı | — |
| E-2 | Promotion kapısı: aktivasyon yalnız aynı release hash'ine ait, platformun doğruladığı geçen eval ile | planlandı | — |
| V | Gate (`--with-integration`) + compose doğrulaması + doküman | planlandı | — |

## L — `GET /v1/incidents`

Bugün bir incident'a yalnız kimliğiyle ulaşılıyor; operatör "son bir saatte ne
açıldı", "hangi soruşturma bütçeye takıldı" sorusunu API'den soramıyor.

- `GET /v1/incidents?status=&severity=&service=&environment=&source=&detected_from=&detected_to=&cursor=&limit=`
- `status` ve `severity` tekrarlanabilir (birden çok değer = VEYA). Bilinmeyen
  değer 400'dür, sessizce yok sayılmaz.
- Sıra `detected_at` azalan, eşitlikte `id` azalan. Sayfalama keyset: `cursor`
  son satırın `(detected_at, id)` ikilisini taşıyan opak bir dizedir; offset
  yoktur (büyük tabloda derin sayfa taraması ve sayfa arası kayma).
- `limit` varsayılan 50, en fazla 200.
- Kiracı doğrulanmış principal'dan gelir; mevcut `ReadIncidents` politikası.
- Satır: kimlik, başlık, servis, ortam, önem, durum, alert sayısı, tespit/
  güncelleme/çözüm zamanları ve **son raporun sonucu** (`Complete`, `Partial`,
  `BudgetExceeded`, …; rapor yoksa `null`). Açıklama, etiketler ve hata metni
  listede yoktur; ayrıntı `GET /v1/incidents/{id}`'dedir.
- İndeks: `(tenant, detected_at, id)`; mevcut `(tenant, status, detected_at)`
  durum filtresini taşır.
- Kabul: Postgres integration — kiracı yalıtımı, her filtre, sayfa sınırında
  kayıp/tekrar olmadan tam gezinme, bozuk cursor 400.

## F — RCA geri bildirimi

Rapor bugün tek yönlü: modelin yanıldığını kimse kayda geçiremiyor, dolayısıyla
hangi uzmanın, hangi incident türünde yanıldığı ölçülemiyor ve eval dataset'i
gerçek hatalardan beslenmiyor.

- `POST /v1/incidents/{id}/report/feedback`:
  `report_version` (zorunlu), `verdict` (`confirmed` | `partially_correct` |
  `incorrect`), `actual_root_cause` (isteğe bağlı, ≤ 2000), `comment` (isteğe
  bağlı, ≤ 2000).
- Aynı kişi aynı rapor sürümü için yeniden gönderirse kaydı günceller (kişi
  başına tek güncel hüküm); timeline'a `report_feedback` girdisi düşer.
- `GET /v1/incidents/{id}/report/feedback`: rapora verilmiş hükümler.
- `GET /v1/report-feedback?verdict=&since=&cursor=&limit=`: kiracı genelinde
  liste; eval dataset'ine aday vakaları seçmenin girdisi.
- Yetki: yazma `WriteIncidents` (incident'ı soruşturan kişi), okuma
  `ReadIncidents`.
- Metrik `sre_report_feedback_total{verdict, outcome}`; etiketler sınırlı.
- Serbest metin hiçbir zaman modele, span'e veya loga gitmez.
- Döngünün kapanışı: `incorrect` hükümlü raporun redakte edilmiş eval vakasına
  dönüştürülmesi RUNBOOK'ta prosedür olarak yazılır. Otomatik dönüşüm kapsam
  dışıdır: vaka fixture'ı insan redaksiyonu ister.
- Kabul: integration — kiracı yalıtımı, var olmayan rapor sürümü 404, upsert,
  liste filtreleri; metrik birim testi.

## E — Eval tamamlama ve promotion kapısı

Bugünkü durum (2026-10-05 incelemesi):

- `POST …/evaluations/{id}` gövdesi serbest `passed` ve `artifact_hash`;
  hash yalnız boş olmamaya bakılıyor, route'taki release id yok sayılıyor.
- Talep eden, tamamlayan ve aktive eden aynı kişi olabiliyor; tamamlayan
  kayıtta bile tutulmuyor (yalnız audit satırı). Üçü de `ManageModelProviders`.
- `HasPassedEvaluationAsync` yalnız `release_id` + `passed` arıyor.
- Harness platform release'ine bağlı değil: candidate manifest'teki release
  id/hash yalnız biçim olarak doğrulanıyor, `summary.json` release'i hiç
  anmıyor. Yani bugün geçen bir eval'in hangi release'i kanıtladığı belli değil.

Release hash'i yalnız deployment version kimliklerini kapsar; deployment ve
provider revision'ları D35 tetikleyicileriyle değişmez olduğundan kimlik
içeriği belirler.

### E-1 — Görev ayrılığı ve artifact doğrulaması

- Tamamlama gövdesi harness'in ürettiği `summary.json`'ın kendisidir
  (`application/json`, bayt bayt). Platform hash'i bu baytlardan kendisi
  hesaplar; CI'daki dosyanın `sha256sum`'ı ile karşılaştırılabilir. Serbest
  `passed` alanı kalkar.
- Harness `sre-eval-summary/v2` yazar: v1 alanlarına ek olarak
  `assignment_release {id, hash}`, `model_deployment {id, version, hash}`,
  `provider_connection_revision {id, version, hash}`, `dataset {id, version}`,
  `baseline {id, bootstrap}`.
- Platform doğrular: şema sürümü; release id/hash route'taki release ile aynı;
  release'in her slotu manifest'teki tek deployment version'a ve onun provider
  revision'ına çözülüyor ve hash'ler tutuyor (harness tek deployment'ı
  değerlendirir; birden çok deployment'a yayılan release'in kanıtı olamaz ve
  reddedilir); dataset `id@version` ve baseline, talepte istenenle aynı;
  `bootstrap` baseline yalnız talep `bootstrap` istediyse kabul.
- Sonuç platformca hesaplanır: `gate.promotion_eligible` ve boş
  `gate.failures` ve `hard_failure_count = 0`. Tutarsızlık 400'dür.
- Tamamlama ayrı bir politika (`model-evaluations:complete`) ve ayrı bir rol
  (`SreEvaluationRunner`, CI kimliği) ister; Operator tamamlayamaz. Tamamlayan
  talep edenden farklı olmalıdır. `completed_by`, `summary_hash` ve talep
  anındaki `release_configuration_hash` kayda yazılır.
- Kabul: integration — doğru summary geçer; yanlış release hash, başka
  deployment, eksik gate, uyumsuz dataset/baseline, aynı aktör, Operator rolü
  reddedilir; hash platformun hesapladığıdır.

### E-2 — Promotion kapısı

- Aktivasyon yalnız şu eval ile geçer: aynı release, kayıttaki
  `release_configuration_hash` güncel hash ile aynı, `passed`, tamamlayan
  aktive edenden farklı.
- Harness'te regresyon karşılaştırması bugün de var (`BaselineGate`); platform
  onu yeniden hesaplamaz, sonucunu ve kimliğini doğrular. Platformun güvendiği
  şey runner kimliği ve görev ayrılığıdır; aritmetiği değil. Bu maliyet
  DECISIONS'a yazılır.
- Kapsam dışı: opsiyonel LLM judge (insan etiketli kalibrasyon seti yok) ve
  baseline'ın aktif release'in eval'inden otomatik türetilmesi.
- Kabul: integration — eval'siz, başarısız eval'li ve tamamlayıcının kendisi
  tarafından aktivasyon reddedilir; doğru akış geçer. Harness testi summary v2
  alanlarını ve bootstrap işaretini kapsar.
