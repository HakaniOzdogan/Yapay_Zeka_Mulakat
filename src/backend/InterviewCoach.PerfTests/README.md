# InterviewCoach.PerfTests

NBomber tabanli backend performans baseline projesi.

## Gereksinimler

- .NET 8 SDK
- Calisan backend API (varsayilan: `http://localhost:8080`)

## Calistirma

```bash
dotnet run --project src/backend/InterviewCoach.PerfTests/InterviewCoach.PerfTests.csproj -c Release
```

## Ortam Degiskenleri

- `PERF_BASE_URL` (varsayilan: `http://localhost:8080`)
- `PERF_USER_EMAIL` (opsiyonel)
- `PERF_USER_PASSWORD` (opsiyonel)
- `PERF_VUS` (varsayilan: `8`)
- `PERF_DURATION_SEC` (varsayilan: `25`)
- `PERF_WARMUP_SEC` (varsayilan: `5`)
- `PERF_RAMP_SEC` (varsayilan: `5`)
- `PERF_EVENTS_BATCH` (varsayilan: `50`)
- `PERF_TRANSCRIPT_BATCH` (varsayilan: `40`)

Not: `PERF_USER_EMAIL` / `PERF_USER_PASSWORD` verilmezse test kullanicisi otomatik kayit edilir.

## AppSettings (opsiyonel)

Varsayilanlar `appsettings.json` icinde `Perf` bolumunden okunur.
Isterseniz `appsettings.local.json` dosyasi olusturup yerel override yapabilirsiniz.
Ortam degiskenleri appsettings degerlerini ezer.

## Senaryolar

1. `events_batch_ingest`
2. `transcript_batch_ingest`
3. `finalize_and_report`

Her senaryo yuk profili:

- warmup: `PERF_WARMUP_SEC` (varsayilan 5s)
- ramp: `PERF_RAMP_SEC` ile `PERF_VUS` seviyesine cikis
- steady: `PERF_DURATION_SEC` boyunca sabit yuk

## Cikti

- NBomber console ozeti
- Rapor dosyalari: `artifacts/perf/` (NBomber HTML/rapor dosyalari)
- Ek ozet JSON: `artifacts/perf/perf_summary_<timestamp>.json`

## Izlenecek metrikler

- p50 / p95 latency
- req/sec
- error rate
- status code dagilimi
