# 13 — REST API Endpoint Referansı

## Base URL

```
http://localhost:8080/api
```

Swagger UI: `http://localhost:8080/swagger`

## Kimlik Doğrulama

Korumalı endpoint'ler `Authorization: Bearer {token}` header'ı gerektirir.

---

## Auth

### POST /auth/register

Yeni kullanıcı kaydı.

```json
// Request
{ "email": "user@example.com", "password": "SecurePass123" }

// Response 200
{ "token": "eyJhbG...", "email": "user@example.com", "role": "User" }
```

### POST /auth/login

Kullanıcı girişi.

```json
// Request
{ "email": "user@example.com", "password": "SecurePass123" }

// Response 200
{ "token": "eyJhbG...", "email": "user@example.com", "role": "User" }
```

---

## Sessions

### POST /sessions

Yeni mülakat oturumu oluştur.

```json
// Request
{ "selectedRole": "Backend Developer", "language": "tr" }

// Response 201
{
  "sessionId": "uuid",
  "selectedRole": "Backend Developer",
  "language": "tr",
  "status": "Active",
  "createdAt": "2026-05-13T10:00:00Z"
}
```

### GET /sessions/{sessionId}

Oturum detayını getir.

### GET /sessions/{sessionId}/questions

Oturuma atanmış soruları getir.

```json
// Response 200
[
  { "id": "uuid", "order": 1, "prompt": "Mikroservis mimarisinde...", "audioUrl": null },
  { "id": "uuid", "order": 2, "prompt": "REST API tasarımında...", "audioUrl": null }
]
```

### POST /sessions/{sessionId}/events/batch

Vision/audio metrik batch gönderimi.

```json
// Request
[
  {
    "clientEventId": "uuid",
    "tsMs": 4500,
    "source": "Vision",
    "type": "vision_metrics_v1",
    "payload": {
      "eyeContact": 0.82,
      "posture": 0.74,
      "fidget": 0.21,
      "headJitter": 0.18,
      "eyeOpenness": 0.65,
      "calibrated": true
    }
  }
]

// Response 200
{ "accepted": 1, "duplicates": 0 }
```

### POST /sessions/{sessionId}/transcript/batch

Transkript segment batch gönderimi.

```json
// Request
[
  {
    "clientSegmentId": "uuid",
    "startMs": 1200,
    "endMs": 3600,
    "text": "Merhaba, ben bu konuda tecrübeliyim.",
    "confidence": 0.93
  }
]

// Response 200
{ "accepted": 1, "duplicates": 0 }
```

### POST /sessions/{sessionId}/finalize

Oturumu sonlandır ve skor hesapla.

```json
// Response 200
{
  "sessionId": "uuid",
  "status": "Finalized",
  "scoreCard": {
    "eyeContactScore": 85,
    "speakingRateScore": 78,
    "fillerScore": 72,
    "postureScore": 90,
    "overallScore": 82
  }
}
```

---

## Reports

### GET /reports/{sessionId}

Tam rapor verisini getir.

```json
// Response 200
{
  "session": { "id": "uuid", "selectedRole": "...", "language": "tr", "createdAt": "..." },
  "scoreCard": {
    "eyeContactScore": 85,
    "speakingRateScore": 78,
    "fillerScore": 72,
    "postureScore": 90,
    "overallScore": 82
  },
  "feedbackItems": [
    {
      "category": "vision",
      "severity": 3,
      "title": "Göz temasında düşüş",
      "details": "2. soruda göz teması %40'a düştü",
      "suggestion": "Kameraya bakmaya devam edin"
    }
  ],
  "questions": [
    { "id": "uuid", "order": 1, "prompt": "...", "audioUrl": "/uploads/..." }
  ],
  "transcript": {
    "full_text": "Merhaba, ben bu konuda..."
  }
}
```

### GET /reports/{sessionId}/export/json

Raporu JSON dosyası olarak indir.

### GET /reports/{sessionId}/export/markdown

Raporu Markdown dosyası olarak indir.

---

## LLM Coaching

### GET /sessions/{sessionId}/llm/coach

Mevcut coaching verisini getir (varsa).

### POST /sessions/{sessionId}/llm/coach

Yeni coaching üret (Claude API çağrısı).

```json
// Response 200
{
  "rubric": {
    "technical_correctness": 3,
    "depth": 2,
    "structure": 4,
    "clarity": 3,
    "confidence": 4
  },
  "overall": 68,
  "feedback": [ ... ],
  "drills": [ ... ]
}
```

### GET /sessions/{sessionId}/evidence-summary

Evidence summary'yi görüntüle (debug amaçlı).

---

## Scoring Profiles

### GET /scoring-profiles

Tanımlı puanlama profillerini listele.

### POST /sessions/{sessionId}/scoring/preview

Seçilen profil ile skor önizleme.

```json
// Request
{ "profileName": "technical" }

// Response 200
{
  "profileName": "technical",
  "currentStoredScoreCard": { ... },
  "scoreCardPreview": { ... }
}
```

### PUT /sessions/{sessionId}/scoring/profile

Oturumun puanlama profilini değiştir.

---

## Admin

### POST /admin/batch-coaching

Toplu coaching işi başlat.

### GET /admin/batch-coaching/{jobId}

İş durumunu sorgula.

### GET /admin/users

Kullanıcı listesi (admin only).

---

## Hata Yanıtları

| HTTP Kodu | Açıklama |
|-----------|----------|
| 400 | Geçersiz istek (validasyon hatası) |
| 401 | Kimlik doğrulama başarısız (token yok/geçersiz) |
| 403 | Yetki yok (admin gerekli) |
| 404 | Kaynak bulunamadı |
| 429 | Rate limit aşıldı |
| 500 | Sunucu hatası |
| 502 | LLM provider hatası |

Hata yanıt formatı:
```json
{ "detail": "Session not found.", "status": 404 }
```
