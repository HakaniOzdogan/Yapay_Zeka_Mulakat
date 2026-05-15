# 03 — API Endpoint'leri

Tüm API route'ları Next.js 14 App Router altında `src/app/api/` dizininde bulunur. Rate limiting Upstash Redis ile sağlanır. Tüm endpoint'ler Zod validation kullanır.

## Authentication

```
POST   /api/auth/register              Email + şifre ile kayıt
POST   /api/auth/[...nextauth]         NextAuth handler (login, logout, OAuth)
POST   /api/auth/forgot-password       Şifre sıfırlama email'i gönder
POST   /api/auth/reset-password        Yeni şifre belirle
GET    /api/auth/verify-email?token=x  Email doğrulama
```

## Kullanıcı

```
GET    /api/user/profile               Profil bilgilerini getir
PUT    /api/user/profile               Profil güncelle
POST   /api/user/upload-cv             CV dosyası yükle (S3)
GET    /api/user/subscription          Abonelik durumu
POST   /api/user/change-password       Şifre değiştir
DELETE /api/user/account               Hesabı sil (KVKK uyumu — tüm veriler silinir)
```

## Mülakat — CRUD

```
POST   /api/interview/create           Yeni mülakat oluştur
GET    /api/interview/:id              Mülakat detayı
PUT    /api/interview/:id              Mülakat güncelle
DELETE /api/interview/:id              Mülakat sil
GET    /api/interview/list             Kullanıcının mülakatları (pagination)
```

### POST /api/interview/create — Örnek

İstek:
```json
{
  "position": "Frontend Developer",
  "companyName": "Google",
  "industry": "Teknoloji",
  "experienceLevel": "mid",
  "interviewType": "MIXED",
  "language": "tr",
  "questionCount": 10,
  "difficulty": "medium",
  "jobDescription": "React, TypeScript, Next.js deneyimi..."
}
```

Cevap:
```json
{
  "interview": {
    "id": "clx...",
    "status": "DRAFT",
    "title": "Google — Frontend Developer Mülakatı",
    "totalQuestions": 10
  }
}
```

## Mülakat — Akış

```
POST   /api/interview/:id/start               Mülakatı başlat, ilk soruyu üret
POST   /api/interview/:id/pause               Duraklat
POST   /api/interview/:id/resume              Devam et
POST   /api/interview/:id/complete             Mülakatı bitir, analiz sürecini başlat
```

## Mülakat — Sorular ve Cevaplar

```
POST   /api/interview/:id/generate-question    Sonraki soruyu AI ile üret
GET    /api/interview/:id/questions             Tüm soruları listele
POST   /api/interview/:id/submit-answer        Cevap gönder (audio + video blob)
POST   /api/interview/:id/upload-recording      Sürekli kayıt dosyasını yükle
```

### POST /api/interview/:id/submit-answer

Bu endpoint, cevap bittiğinde çağrılır. `multipart/form-data` olarak:

```
Fields:
  - questionId: string
  - startedAt: ISO timestamp
  - submittedAt: ISO timestamp
  - duration: number (saniye)

Files:
  - videoClip: Blob (webm, soru bazlı video clip)
  - audioClip: Blob (webm, sadece ses kanalı)
```

İşlem sırası:
1. Video clip → S3'e upload
2. Audio clip → S3'e upload
3. Audio clip → ElevenLabs Scribe v2 Batch API'ye gönder (async)
4. `InterviewResponse` kaydı oluştur (transcriptionStatus: PENDING)
5. Sonraki soruyu üret (GPT-4)
6. Cevap olarak sonraki soru döndür

Cevap:
```json
{
  "responseId": "clx...",
  "transcriptionStatus": "PROCESSING",
  "nextQuestion": {
    "id": "clx...",
    "questionOrder": 4,
    "questionText": "Bir ekipte çatışma yaşadığınız bir durumu anlatır mısınız?",
    "questionType": "behavioral",
    "difficulty": "medium"
  }
}
```

## Transkripsiyon (Webhook / Polling)

```
POST   /api/transcription/webhook      ElevenLabs webhook callback
GET    /api/transcription/:jobId        Transkripsiyon durumu sorgula
```

### Transkripsiyon akışı

ElevenLabs Scribe v2 Batch async çalışır. İki yöntemle sonuç alınabilir:

1. **Webhook (tercih edilen):** ElevenLabs tamamlandığında `/api/transcription/webhook` endpoint'ine POST atar.
2. **Polling:** Mülakat bittiğinde tüm pending transkriptler için polling yapılır.

## Şirket Verileri

```
POST   /api/company/search             Şirket ara (autocomplete)
GET    /api/company/:name              Şirket bilgileri (cache'ten veya API'den)
POST   /api/company/analyze            AI ile şirket analizi ve mülakat stratejisi
```

### GET /api/company/:name — Akış

1. Redis cache kontrol (`company:{name}`, TTL 7 gün)
2. Cache varsa → direkt dön
3. Cache yoksa → paralel olarak:
   - RapidAPI LinkedIn Scraper
   - Web arama (şirket haberleri)
4. GPT-4 ile şirket profili ve mülakat stratejisi oluştur
5. Redis'e cache'le
6. Dön

## Analiz ve Rapor

```
GET    /api/analysis/:interviewId      Analiz raporunu getir
POST   /api/analysis/:interviewId/generate   Rapor oluştur (mülakat bittikten sonra)
GET    /api/analysis/:interviewId/pdf  PDF export (premium)
GET    /api/analysis/history           Geçmiş analizler
GET    /api/analysis/compare/:id1/:id2 İki mülakatı karşılaştır
```

## Ödeme

```
POST   /api/payment/create-checkout    Stripe checkout session oluştur
POST   /api/payment/webhook            Stripe webhook handler
GET    /api/payment/history            Ödeme geçmişi
POST   /api/payment/cancel-subscription Abonelik iptali
```

## Admin

```
GET    /api/admin/dashboard            Dashboard metrikleri
GET    /api/admin/users                Kullanıcı listesi
PUT    /api/admin/user/:id             Kullanıcı düzenle
GET    /api/admin/interviews           Tüm mülakatlar
GET    /api/admin/metrics              Sistem metrikleri (AI maliyet, kullanım)
```

## Python FastAPI Endpoint'leri (AI Servisi)

Ayrı serviste çalışır (varsayılan: `http://localhost:8000`).

```
POST   /analyze-audio                  Ses dosyası analizi (pitch, tempo, duraksamalar)
POST   /analyze-video-batch            Video dosyası batch analizi (yüz, duruş)
GET    /health                         Servis sağlık kontrolü
```

## Rate Limiting

| Endpoint Grubu | Limit | Pencere |
|----------------|-------|---------|
| Genel API | 100 istek | 15 dakika |
| AI endpoint'leri (`/generate-question`, `/analyze`) | 10 istek | 1 dakika |
| Auth (`/register`, `/login`) | 5 istek | 15 dakika |
| Dosya upload | 20 istek | 1 saat |

Rate limit aşıldığında: `429 Too Many Requests` + `Retry-After` header.
