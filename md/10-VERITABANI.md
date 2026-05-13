# 10 — Veritabanı Şeması ve Veri Modeli

## Genel Bakış

Sistem PostgreSQL veritabanı kullanır. EF Core Code-First yaklaşımı ile şema yönetilir. Migration'lar `InterviewCoach.Infrastructure/Migrations` altında saklanır.

## ER Diyagramı

```
┌──────────────┐     1:N     ┌──────────────────┐
│    Users     │────────────▶│    Sessions      │
│              │             │                  │
│ Id           │             │ Id               │
│ Email        │             │ UserId (FK)      │
│ PasswordHash │             │ SelectedRole     │
│ Role         │             │ Language         │
│ CreatedAt    │             │ Status           │
└──────────────┘             │ ScoringProfile   │
                             │ CreatedAt        │
                             │ FinishedAt       │
                             └────────┬─────────┘
                                      │
                    ┌─────────────────┼─────────────────────┐
                    │ 1:N             │ 1:N                  │ 1:N
                    ▼                 ▼                      ▼
          ┌─────────────────┐ ┌──────────────────┐ ┌────────────────────┐
          │   Questions     │ │  MetricEvents    │ │TranscriptSegments  │
          │                 │ │                  │ │                    │
          │ Id              │ │ Id               │ │ Id                 │
          │ SessionId (FK)  │ │ SessionId (FK)   │ │ SessionId (FK)     │
          │ Order           │ │ ClientEventId    │ │ ClientSegmentId    │
          │ Prompt          │ │ TsMs             │ │ StartMs            │
          │ AudioUrl        │ │ Source           │ │ EndMs              │
          │ CreatedAt       │ │ Type             │ │ Text               │
          └─────────────────┘ │ Payload (JSON)   │ │ Confidence         │
                              └──────────────────┘ └────────────────────┘
                                      │
                                      │ 1:N
                    ┌─────────────────┼──────────────────┐
                    ▼                                     ▼
          ┌─────────────────┐                   ┌─────────────────┐
          │   ScoreCards    │                   │ FeedbackItems    │
          │                 │                   │                  │
          │ Id              │                   │ Id               │
          │ SessionId (FK)  │                   │ SessionId (FK)   │
          │ EyeContactScore │                   │ Category         │
          │ SpeakingRateScr │                   │ Severity         │
          │ FillerScore     │                   │ Title            │
          │ PostureScore    │                   │ Details          │
          │ OverallScore    │                   │ Suggestion       │
          │ CreatedAt       │                   │ ExampleText      │
          └─────────────────┘                   └─────────────────┘

          ┌─────────────────┐
          │    LlmRuns      │
          │                 │
          │ Id              │
          │ SessionId (FK)  │
          │ Provider        │
          │ Model           │
          │ InputHash       │
          │ OutputJson      │
          │ DurationMs      │
          │ Success         │
          │ CreatedAt       │
          └─────────────────┘
```

## Tablo Detayları

### Users

Kullanıcı hesap bilgileri.

| Kolon | Tip | Açıklama |
|-------|-----|----------|
| Id | UUID | Birincil anahtar |
| Email | VARCHAR(255) | Benzersiz e-posta |
| PasswordHash | VARCHAR(500) | BCrypt hash |
| Role | VARCHAR(50) | "User" veya "Admin" |
| CreatedAt | TIMESTAMP | Oluşturulma tarihi |

### Sessions

Mülakat oturumları.

| Kolon | Tip | Açıklama |
|-------|-----|----------|
| Id | UUID | Birincil anahtar |
| UserId | UUID (FK) | Users tablosuna referans |
| SelectedRole | VARCHAR(200) | Seçilen mülakat alanı |
| Language | VARCHAR(10) | "tr" veya "en" |
| Status | VARCHAR(50) | "Active", "Finalized", "Abandoned" |
| ScoringProfile | VARCHAR(100) | Uygulanan puanlama profili |
| CreatedAt | TIMESTAMP | Başlangıç zamanı |
| FinishedAt | TIMESTAMP | Bitiş zamanı |

### Questions

Mülakat soruları ve kullanıcı yanıt kayıtları.

| Kolon | Tip | Açıklama |
|-------|-----|----------|
| Id | UUID | Birincil anahtar |
| SessionId | UUID (FK) | Sessions referansı |
| Order | INT | Soru sırası (1, 2, 3...) |
| Prompt | TEXT | Soru metni |
| AudioUrl | VARCHAR(500) | Yanıt video/ses dosya yolu |
| CreatedAt | TIMESTAMP | Oluşturulma zamanı |

### MetricEvents

Mülakat sırasında toplanan vision ve ses metrikleri.

| Kolon | Tip | Açıklama |
|-------|-----|----------|
| Id | BIGINT | Birincil anahtar (auto-increment) |
| SessionId | UUID (FK) | Sessions referansı |
| ClientEventId | VARCHAR(100) | Frontend tarafından üretilen benzersiz ID |
| TsMs | BIGINT | Oturum başlangıcından itibaren milisaniye |
| Source | VARCHAR(50) | "Vision" veya "Audio" |
| Type | VARCHAR(100) | "vision_metrics_v1" |
| Payload | JSONB | Metrik değerleri (PostgreSQL JSON) |

### TranscriptSegments

Transkript segmentleri.

| Kolon | Tip | Açıklama |
|-------|-----|----------|
| Id | BIGINT | Birincil anahtar |
| SessionId | UUID (FK) | Sessions referansı |
| ClientSegmentId | VARCHAR(100) | Deduplication için benzersiz ID |
| StartMs | BIGINT | Segment başlangıç zamanı (ms) |
| EndMs | BIGINT | Segment bitiş zamanı (ms) |
| Text | TEXT | Transkript metni |
| Confidence | FLOAT | ASR güven skoru (0.0-1.0) |

### ScoreCards

Hesaplanmış skorlar.

| Kolon | Tip | Açıklama |
|-------|-----|----------|
| Id | UUID | Birincil anahtar |
| SessionId | UUID (FK) | Sessions referansı |
| EyeContactScore | INT | 0-100 |
| SpeakingRateScore | INT | 0-100 |
| FillerScore | INT | 0-100 |
| PostureScore | INT | 0-100 |
| OverallScore | INT | 0-100 ağırlıklı ortalama |

### FeedbackItems

Pattern-based feedback kartları (finalize sırasında oluşur).

| Kolon | Tip | Açıklama |
|-------|-----|----------|
| Id | UUID | Birincil anahtar |
| SessionId | UUID (FK) | Sessions referansı |
| Category | VARCHAR(50) | Feedback kategorisi |
| Severity | INT | 1-5 ciddiyet seviyesi |
| Title | VARCHAR(500) | Başlık |
| Details | TEXT | Detaylı açıklama |
| Suggestion | TEXT | İyileştirme önerisi |

### LlmRuns

LLM API çağrı logları.

| Kolon | Tip | Açıklama |
|-------|-----|----------|
| Id | UUID | Birincil anahtar |
| SessionId | UUID (FK) | Sessions referansı |
| Provider | VARCHAR(50) | "Anthropic" veya "Ollama" |
| Model | VARCHAR(100) | Kullanılan model adı |
| InputHash | VARCHAR(64) | Input deduplication hash |
| OutputJson | TEXT | LLM çıktısı |
| DurationMs | INT | Çağrı süresi |
| Success | BOOLEAN | Başarılı mı |
| CreatedAt | TIMESTAMP | Çağrı zamanı |

## İlişkiler

- User (1) → (N) Session
- Session (1) → (N) Question
- Session (1) → (N) MetricEvent
- Session (1) → (N) TranscriptSegment
- Session (1) → (1) ScoreCard
- Session (1) → (N) FeedbackItem
- Session (1) → (N) LlmRun

## Migration Yönetimi

```bash
# Yeni migration oluştur
cd src/backend/InterviewCoach.Api
dotnet ef migrations add MigrationName

# Migration'ları uygula
dotnet ef database update

# Son migration'ı geri al
dotnet ef migrations remove
```

## Bağlantı Yapılandırması

```json
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Database=interviewcoach;Username=coach;Password=coachpass"
  }
}
```
