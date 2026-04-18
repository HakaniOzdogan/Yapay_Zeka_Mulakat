# Golden Session Regression Tests

Bu klasor replay fixture tabanli regression test verilerini icerir.

## Calistirma

```bash
dotnet test src/backend/InterviewCoach.Tests/InterviewCoach.Tests.csproj
```

## Snapshot Guncelleme (bilincli)

Beklenen snapshot'lari guncellemek icin:

```bash
$env:UPDATE_GOLDEN_SNAPSHOTS = "true"
dotnet test src/backend/InterviewCoach.Tests/InterviewCoach.Tests.csproj --filter "FullyQualifiedName~GoldenSession"
```

Bu modda testler assertion yerine normalize edilmis mevcut ciktilari:

- `Expected/report.snapshot.json`
- `Expected/evidence-summary.snapshot.json`

dosyalarina yazar.

## Opsiyonel LLM Schema Testi

```bash
$env:RUN_LLM_REGRESSION = "true"
dotnet test src/backend/InterviewCoach.Tests/InterviewCoach.Tests.csproj --filter "FullyQualifiedName~LlmCoach"
```

Ollama/LLM erisilemezse test skip olur.

## Toleranslar

- `scoreCard.*` numeric alanlari: +/- 3
- `signals.*` numeric alanlari: +/- 0.05
- Diger numeric alanlar: birebir

Volatile alanlar (`id`, `sessionId`, `createdAt*`, `traceId` vb.) normalize edilerek karsilastirmadan cikarilir.
