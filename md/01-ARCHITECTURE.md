# 01 — Sistem Mimarisi

## Genel Bakış

Platform, üç ana servis katmanından oluşur: Next.js frontend/API, Python AI microservice ve veri katmanı (PostgreSQL + Redis). MongoDB orijinal tasarımdan çıkarılmıştır — MVP karmaşıklığını azaltmak için tüm veri PostgreSQL + Redis ile yönetilir.

```
┌──────────────────────────────────────────────────────────────┐
│                     CLIENT (Tarayıcı)                        │
│  ┌────────────────────────────────────────────────────────┐  │
│  │  Next.js Frontend (React + TypeScript)                 │  │
│  │  ├─ Canvas Compositor (Ekran + Webcam PiP)             │  │
│  │  ├─ MediaRecorder (Sürekli + Soru bazlı clip)          │  │
│  │  ├─ MediaPipe Face Mesh (Real-time yüz analizi)        │  │
│  │  └─ Zustand State Management                           │  │
│  └────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────┘
                          ↓ HTTPS
┌──────────────────────────────────────────────────────────────┐
│                   NEXT.JS API KATMANI                        │
│  ┌────────────────┐  ┌─────────────────┐  ┌──────────────┐  │
│  │ NextAuth.js v5 │  │  İş Mantığı     │  │ Rate Limiter │  │
│  │ (JWT Sessions) │  │  Controllers    │  │ (Upstash)    │  │
│  └────────────────┘  └─────────────────┘  └──────────────┘  │
└──────────────────────────────────────────────────────────────┘
          ↓                    ↓                    ↓
┌──────────────┐   ┌───────────────────┐   ┌───────────────┐
│ PostgreSQL   │   │ Python FastAPI    │   │    Redis      │
│ (Prisma ORM) │   │ AI Microservice   │   │ (Upstash)     │
│              │   │                   │   │               │
│ • Users      │   │ • Ses analizi     │   │ • Rate limit  │
│ • Interviews │   │ • Video analizi   │   │ • Şirket cache│
│ • Analytics  │   │ • ML inference    │   │ • Session     │
│ • Payments   │   │                   │   │               │
└──────────────┘   └───────────────────┘   └───────────────┘
                          ↓
                ┌───────────────────┐
                │  External APIs    │
                │ • ElevenLabs STT  │
                │ • OpenAI GPT-4    │
                │ • AWS S3          │
                │ • Stripe/Iyzico   │
                └───────────────────┘
```

## Orijinal Tasarımdan Farklılıklar

| Konu | Eski Tasarım | Yeni Tasarım | Sebep |
|------|-------------|--------------|-------|
| Veritabanı | PostgreSQL + MongoDB + Redis | PostgreSQL + Redis | MVP karmaşıklığını azaltır, veri tutarsızlığı riskini ortadan kaldırır |
| STT | Whisper API (real-time, 3sn chunk) | ElevenLabs Scribe v2 Batch | Doğruluk: %8.4 WER → ≤%5 WER. Live transcript kaldırıldı |
| Real-time | Socket.io | Kaldırıldı (MVP'de yok) | Next.js 14 App Router ile uyumsuz. Pusher/Ably ile sonra eklenebilir |
| Video kayıt | Sadece ses | Ekran + webcam PiP (Canvas compositor) | Tam mülakat deneyimi kaydı |
| Video yapısı | Tek dosya | Sürekli kayıt + soru bazlı clip'ler | Raporda soru bazlı geri bildirim |

## Servis Sorumlulukları

### 1. Next.js Frontend + API (ana uygulama)

Tüm kullanıcı arayüzü, authentication, iş mantığı ve API route'ları burada çalışır. Vercel'e deploy edilir.

Sorumluluklar: sayfa render, form validation, video/ses kaydı (client-side), MediaPipe ile real-time yüz analizi, S3'e dosya upload, Prisma ile veritabanı işlemleri, NextAuth ile kimlik doğrulama.

### 2. Python FastAPI Microservice (AI servisi)

Video ve ses dosyalarının batch analizi için ayrı bir servis. AWS EC2 veya Render'a deploy edilir.

Sorumluluklar: librosa ile ses özellik çıkarma (pitch, tempo, enerji, duraksamalar), video frame analizi (batch), ML model inference, ElevenLabs Scribe v2 API çağrıları (transkripsiyon).

### 3. PostgreSQL (Prisma)

Tüm kalıcı veri burada saklanır: kullanıcılar, mülakatlar, sorular, cevaplar, analizler, ödemeler, başarımlar.

### 4. Redis (Upstash)

Geçici ve sık erişilen veriler: API rate limiting, şirket veri cache'i (TTL: 7 gün), session verileri.

## Veri Akışları

### Mülakat başlatma akışı

```
Kullanıcı "Mülakatı Başlat" → 
  Frontend: getDisplayMedia() + getUserMedia() izin al →
  Frontend: Canvas compositor başlat (ekran + webcam PiP) →
  Frontend: Sürekli MediaRecorder.start() →
  API: POST /api/interview/:id/start →
  API: GPT-4 ile ilk soruyu üret →
  Frontend: Soruyu göster + Clip MediaRecorder.start()
```

### Cevap gönderme akışı

```
Kullanıcı "Sonraki Soru" butonuna basar →
  Frontend: Clip MediaRecorder.stop() → blob oluşur →
  Frontend: Audio blob'u ayır (ses kanalı) →
  API: POST /api/interview/:id/submit-answer (audio + video blob) →
    ├─ S3'e video clip upload (arka plan)
    ├─ ElevenLabs Scribe v2 Batch'e audio gönder (arka plan)
    └─ GPT-4 ile sonraki soruyu üret →
  Frontend: Yeni soruyu göster + yeni Clip MediaRecorder.start() →
  (Kullanıcı beklemez, transcript arka planda tamamlanır)
```

### Mülakat bitirme akışı

```
Kullanıcı "Mülakatı Bitir" →
  Frontend: Her iki MediaRecorder.stop() →
  Frontend: Sürekli kayıt blob'unu S3'e upload →
  API: POST /api/interview/:id/complete →
    ├─ Tüm transcript'lerin tamamlanmasını bekle
    ├─ Python servise batch video/ses analizi gönder
    ├─ GPT-4 ile kapsamlı rapor oluştur
    └─ InterviewAnalysis tablosuna kaydet →
  Frontend: Rapor sayfasına yönlendir
```

## Dosya Depolama Stratejisi

Tüm video ve ses dosyaları AWS S3 (veya Cloudflare R2) üzerinde saklanır.

```
s3://interview-platform/
├── users/{userId}/
│   └── interviews/{interviewId}/
│       ├── full-recording.webm          # Sürekli kayıt (ekran + webcam PiP)
│       ├── clips/
│       │   ├── q1-clip.webm             # Soru 1 video clip
│       │   ├── q1-audio.webm            # Soru 1 sadece ses
│       │   ├── q2-clip.webm
│       │   ├── q2-audio.webm
│       │   └── ...
│       └── report/
│           └── report.pdf               # Oluşturulan PDF rapor (premium)
```

## Güvenlik Katmanları

1. **Authentication:** NextAuth.js v5, JWT session (30 gün), Google OAuth
2. **Rate limiting:** Upstash Redis ile API bazlı limit (100 req/15dk genel, 10 req/dk AI endpoint'leri)
3. **Input validation:** Zod şemaları tüm API route'larında
4. **SQL injection:** Prisma ORM parametrize sorguları
5. **XSS:** React otomatik escape + Content Security Policy
6. **CORS:** Sadece FRONTEND_URL origin'ine izin
7. **Dosya upload:** Dosya tipi ve boyut kontrolü (video max 500MB, ses max 50MB)
8. **KVKK/GDPR:** Video ve ses verileri şifreli saklanır, kullanıcı silme endpoint'i mevcut
