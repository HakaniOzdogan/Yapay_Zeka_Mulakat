# 02 — Sistem Mimarisi

## Genel Bakış

Interview AI, 5 ana bileşenden oluşan bir mikro-servis mimarisi kullanır. Her bileşen bağımsız çalışabilir ve Docker ile konteynerize edilebilir.

```
┌─────────────────────────────────────────────────────────────────┐
│                        KULLANICI (Tarayıcı)                     │
│         Kamera + Mikrofon → Video Kayıt + Ses Kayıt             │
└──────────┬──────────────┬───────────────────┬───────────────────┘
           │              │                   │
     HTTP/REST      WebSocket           MediaRecorder
           │              │                   │
┌──────────▼──────────────▼───────────────────▼───────────────────┐
│                     REACT FRONTEND                               │
│  ┌──────────┐  ┌──────────────┐  ┌───────────┐  ┌────────────┐ │
│  │ Sayfa    │  │ MediaPipe    │  │ Streaming │  │ Video      │ │
│  │ Router   │  │ Vision       │  │ ASR       │  │ Canvas     │ │
│  └──────────┘  └──────────────┘  └───────────┘  └────────────┘ │
└──────────┬──────────────────────────────┬───────────────────────┘
           │ HTTP/REST                    │ WebSocket
┌──────────▼──────────────────┐  ┌───────▼───────────────────────┐
│    ASP.NET CORE WEB API     │  │    SPEECH SERVICE (FastAPI)    │
│  ┌────────────────────────┐ │  │  ┌─────────────────────────┐  │
│  │ Session Controller     │ │  │  │ Whisper ASR Engine       │  │
│  │ Report Controller      │ │  │  │ Streaming Transcription  │  │
│  │ LLM Coach Controller   │ │  │  │ Language Detection       │  │
│  │ Auth Controller        │ │  │  └─────────────────────────┘  │
│  │ Admin Controller       │ │  └───────────────────────────────┘
│  └───────────┬────────────┘ │
│  ┌───────────▼────────────┐ │
│  │ LLM Coaching Service   │ │
│  │ Scoring Engine          │ │
│  │ Evidence Summarizer     │ │
│  │ Batch Coaching Job      │ │
│  └───────────┬────────────┘ │
└──────────────┼──────────────┘
               │
     ┌─────────┼─────────┐
     ▼                   ▼
┌──────────┐    ┌────────────────┐
│PostgreSQL│    │  LLM Provider  │
│ EF Core  │    │ ┌────────────┐ │
│          │    │ │Claude API  │ │  ← Primary
│ Sessions │    │ │(Anthropic) │ │
│ Events   │    │ └────────────┘ │
│ Scores   │    │ ┌────────────┐ │
│ Transcr. │    │ │Ollama      │ │  ← Fallback
│ Users    │    │ │(Local)     │ │
└──────────┘    │ └────────────┘ │
                └────────────────┘
```

## Bileşen Detayları

### 1. React Frontend

React 18 + TypeScript + Vite tabanlı tek sayfa uygulaması (SPA).

**Sorumluluklar:**
- Kullanıcı kayıt/giriş arayüzü
- Mülakat oturumu yönetimi (kamera, mikrofon, soru akışı)
- MediaPipe ile istemci tarafında gerçek zamanlı görüntü analizi
- Streaming ASR bağlantısı (WebSocket üzerinden Speech Service'e)
- Metrik ve transkript verilerinin backend'e gönderilmesi
- Rapor görüntüleme ve dışa aktarma

**Kritik Servisler:**
- `MediaPipeService.ts` — Face Mesh ve Pose landmark tespiti
- `MetricsComputer.ts` — Ham landmark verilerinden metrik hesaplama
- `AudioAnalyzer.ts` — Ses seviyesi ve aktivite tespiti
- `streamingAsr.ts` — WebSocket üzerinden canlı transkript
- `SessionTransport.ts` — Metrik batch'lerini backend'e iletme
- `ApiService.ts` — Tüm REST API çağrıları

### 2. ASP.NET Core Web API

.NET 8 tabanlı RESTful API backend.

**Sorumluluklar:**
- Kullanıcı kimlik doğrulama ve yetkilendirme (JWT)
- Mülakat oturumu CRUD işlemleri
- Metrik ve transkript batch ingestion
- Oturum finalize ve skor hesaplama
- LLM coaching orchestration (retry, fallback, cache)
- Rapor aggregation ve dışa aktarma
- Admin panel API'leri (batch coaching, kullanıcı yönetimi)

**Katmanlar:**
- `InterviewCoach.Api` — Controller'lar, servisler, konfigürasyon
- `InterviewCoach.Domain` — Domain modelleri ve entity'ler
- `InterviewCoach.Infrastructure` — EF Core DbContext, migrations
- `InterviewCoach.Application` — İş mantığı abstraction'ları

### 3. Speech Service

Python FastAPI tabanlı konuşma tanıma servisi.

**Sorumluluklar:**
- Whisper modeli ile ses dosyasından transkript çıkarma
- WebSocket üzerinden streaming ASR (gerçek zamanlı)
- Dil tespiti (Türkçe/İngilizce)
- Filler word (dolgu kelime) ve duraklama sayımı

### 4. PostgreSQL Veritabanı

EF Core ile yönetilen ilişkisel veritabanı.

**Ana Tablolar:** Sessions, Questions, MetricEvents, TranscriptSegments, ScoreCards, FeedbackItems, Users, LlmRuns

### 5. LLM Provider Katmanı

Çift provider mimarisi ile yüksek erişilebilirlik.

**Claude API (Primary):** Yüksek kalite yetkinlik değerlendirmesi. `claude-sonnet-4-6` veya `claude-opus-4-6` modeli.

**Ollama (Fallback):** Yerel çalışan LLM. Claude erişilemez olduğunda devreye girer. `qwen2.5:7b-instruct` modeli.

## Veri Akışı

### Mülakat Oturumu Sırası

```
1. Kullanıcı "Mülakata Başla" → POST /api/sessions (session oluştur)
2. Sorular yüklenir             → GET /api/sessions/{id}/questions
3. Kayıt başlar:
   a. MediaPipe frame analiz    → Her 500ms metrik hesapla
   b. Metrik batch              → POST /api/sessions/{id}/events/batch
   c. Ses WebSocket'e akar      → Speech Service transkript üretir
   d. Transkript batch          → POST /api/sessions/{id}/transcript/batch
   e. Video kaydı               → MediaRecorder blob olarak saklar
4. Soru geçişi                  → Ses blob backend'e upload edilir
5. Son soru biter               → POST /api/sessions/{id}/finalize
6. Finalize:
   a. ScoreCard hesaplanır (metrik ağırlıklarına göre)
   b. FeedbackItems oluşturulur (pattern detection)
   c. Rapor hazır
7. Rapor sayfası açılır          → GET /api/reports/{id}
8. AI Coaching istenir           → POST /api/sessions/{id}/llm/coach
   a. Evidence summary hazırlanır
   b. Claude API'ye gönderilir
   c. JSON yanıt parse edilir
   d. Coaching raporu döndürülür
```

## Güvenlik Mimarisi

- **Kimlik Doğrulama:** JWT Bearer Token (access token 120 dakika)
- **Parola:** BCrypt hash ile saklanır
- **Yetkilendirme:** Role-based (Admin / User)
- **CORS:** Konfigürasyonla izin verilen origin'ler
- **PII Koruması:** Transkript redaction, LLM guardrails
- **Veri Saklama:** Retention policy ile otomatik silme (30 gün)

## Port Yapılandırması

| Servis | Port | Açıklama |
|--------|------|----------|
| Frontend (Vite dev) | 5173 | React geliştirme sunucusu |
| Backend API | 8080 | ASP.NET Core API |
| Speech Service | 8765 | FastAPI + Whisper |
| PostgreSQL | 5432 | Veritabanı |
| Ollama | 11434 | Yerel LLM runtime |
