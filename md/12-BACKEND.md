# 12 — Backend API Rehberi

## Teknoloji Stack

- **ASP.NET Core 8** — Web API framework
- **Entity Framework Core** — ORM
- **PostgreSQL** — Veritabanı
- **JWT Bearer** — Kimlik doğrulama
- **Swagger/OpenAPI** — API dokümantasyonu

## Proje Yapısı

```
src/backend/
├── InterviewCoach.Api/           # Ana API projesi
│   ├── Controllers/              # API endpoint'leri
│   │   ├── SessionsController.cs
│   │   ├── ReportsController.cs
│   │   ├── LlmCoachController.cs
│   │   ├── AuthController.cs
│   │   ├── AdminController.cs
│   │   └── QuestionsController.cs
│   ├── Services/                 # İş mantığı servisleri
│   │   ├── LlmCoachingService.cs      # Coaching prompt + parse
│   │   ├── LlmCoachingOrchestrator.cs # Retry, fallback, cache
│   │   ├── LlmAnalysisService.cs      # Evidence summary üretimi
│   │   ├── OllamaClient.cs            # LLM client (ILlmClient)
│   │   └── BatchCoachingJobService.cs  # Toplu coaching
│   ├── Program.cs               # DI, middleware, konfigürasyon
│   ├── appsettings.json         # Production ayarları
│   └── appsettings.Development.json  # Dev ayarları
│
├── InterviewCoach.Domain/        # Domain modelleri
│   ├── Session.cs
│   ├── Question.cs
│   ├── MetricEvent.cs
│   ├── TranscriptSegment.cs
│   ├── ScoreCard.cs
│   ├── FeedbackItem.cs
│   ├── User.cs
│   └── LlmRun.cs
│
├── InterviewCoach.Infrastructure/ # Altyapı katmanı
│   ├── ApplicationDbContext.cs    # EF Core DbContext
│   └── Migrations/               # Veritabanı migration'ları
│
├── InterviewCoach.Application/    # İş mantığı abstraction
│
└── InterviewCoach.Tests/          # Test projesi
    ├── LlmCoachingOrchestratorTests.cs
    ├── LlmPromptAbEvaluationTests.cs
    ├── BatchCoachingJobServiceTests.cs
    └── Fixtures/                  # Test verileri
```

## Servis Katmanları

### LLM Coaching Pipeline

```
İstek → LlmCoachController
             │
             ▼
    LlmCoachingOrchestrator     ← Retry, fallback, cache yönetimi
             │
             ▼
    LlmAnalysisService          ← Evidence summary hazırla
             │
             ▼
    LlmCoachingService          ← Prompt oluştur, JSON parse/validate
             │
             ▼
    ILlmClient                  ← Claude veya Ollama'ya gönder
    (AnthropicClient / OpenAiResponsesClient)
```

### ILlmClient Interface

```csharp
public interface ILlmClient
{
    Task<LlmJsonResponse> GenerateJsonAsync(
        LlmJsonRequest request, 
        CancellationToken cancellationToken = default);
}
```

Mevcut implementasyon `OpenAiResponsesClient` (OpenAI-compat format). Claude entegrasyonunda `AnthropicClient` eklenecek.

### LlmCoachingOrchestrator

Orchestrator şu adımları yönetir:

1. Evidence summary'yi hazırla (LlmAnalysisService)
2. Optimizasyon tier'ını belirle (small/medium/full)
3. Model routing (complexity'e göre)
4. Primary model ile çağrı yap
5. Başarısız → retry (max 2 deneme)
6. Hâlâ başarısız → fallback modele geç
7. Hâlâ başarısız → cache'e bak
8. Sonucu LlmRun olarak logla

### Scoring Engine

Finalize sırasında çalışır:

1. MetricEvents tablosundan tüm event'leri çek
2. TranscriptSegments'ten transcript birleştir
3. Profil ağırlık ve eşiklerini al
4. Her metrik için 0-100 skor hesapla
5. Ağırlıklı ortalama ile OverallScore hesapla
6. ScoreCard ve FeedbackItems oluştur

## Konfigürasyon Bölümleri

### Llm

LLM provider ayarları (model, API key, timeout, retry, fallback).

### ScoringProfiles

Puanlama profilleri (ağırlıklar, eşik değerler).

### LlmGuardrails

Çıktı güvenlik kontrolleri:
- `MinQualityScore`: Minimum kabul edilir kalite skoru
- `EnableProfanityFilter`: Küfür/uygunsuz içerik filtresi
- `EnablePiiRedaction`: Kişisel bilgi redaction
- `StrictGrounding`: Hallucination kontrolü

### LlmOptimization

Prompt optimizasyonu:
- Tier bazlı prompt boyutu limitleri (small/medium/full)
- Model routing (complexity'e göre farklı model)
- Transcript dilimi uzunlukları

### BatchCoaching

Toplu coaching iş ayarları:
- `MaxParallelism`: Eş zamanlı coaching sayısı
- `MaxSessionsPerJob`: İş başına maksimum oturum
- `PollIntervalSeconds`: İş durumu kontrol sıklığı

### Retention

Veri saklama politikası:
- `DeleteAfterDays`: N gün sonra tamamen sil
- `KeepSummariesOnlyAfterDays`: N gün sonra sadece özet tut

### Auth

JWT ayarları:
- `JwtKey`: İmzalama anahtarı (production'da güçlü ve gizli olmalı)
- `AccessTokenMinutes`: Token geçerlilik süresi
- `SeedAdminEmail/Password`: İlk admin hesabı

## Middleware Pipeline

```
Request
  │
  ├── CORS
  ├── Authentication (JWT)
  ├── Authorization
  ├── Swagger (dev only)
  ├── Routing
  │     ├── /api/auth/*
  │     ├── /api/sessions/*
  │     ├── /api/reports/*
  │     ├── /api/sessions/*/llm/*
  │     └── /api/admin/*
  └── Static Files
```

## Test Yapısı

```bash
# Tüm testleri çalıştır
cd src/backend
dotnet test

# Belirli test projesi
dotnet test InterviewCoach.Tests/
dotnet test InterviewCoach.Api.Tests/
```

Test türleri:
- **Unit tests:** Servis mantığı (LlmCoachingService, MetricsComputer)
- **Integration tests:** API endpoint'leri (WebApplicationFactory)
- **Performance tests:** LLM çağrı süreleri (InterviewCoach.PerfTests)
