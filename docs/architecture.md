# Architecture

Bu belge çalışma zamanı bileşenlerini, depolama modelini, temel veri yapılarını ve hata ayıklama araçlarını açıklar.

## Bileşenler

| Bileşen | Konum | Teknoloji |
|---------|-------|-----------|
| **Frontend** | `src/frontend` | React 18 + TypeScript + Vite |
| **Backend API** | `src/backend/InterviewCoach.Api` | ASP.NET Core 8 |
| **Veritabanı katmanı** | `src/backend/InterviewCoach.Infrastructure` | EF Core + PostgreSQL 16 |
| **Konuşma servisi** | `services/speech-service` | FastAPI + Faster-Whisper |
| **LLM (yerel)** | `ollama` container | Ollama (opsiyonel) |
| **LLM (bulut)** | — | OpenAI API veya Anthropic API |
| **Stitch AI SDK** | `src/frontend/src/services/StitchService.ts` | @google/stitch-sdk |

### Frontend Sayfaları

| Sayfa | Rota | Açıklama |
|-------|------|----------|
| `Home.tsx` | `/` | Mülakat başlatma, zorluk seçimi |
| `AuthPage.tsx` | `/auth` | Kayıt ve giriş |
| `InterviewSession.tsx` | `/interview/:sessionId` | Aktif mülakat (video, soru akışı, model durum paneli) |
| `Report.tsx` | `/report/:sessionId` | Mülakat raporu, transcript, AI koçluk |
| `ReportsList.tsx` | `/reports` | Geçmiş mülakat listesi |
| `AdminPage.tsx` | `/admin` | Kullanıcı yönetimi, toplu koçluk işleri, bekletme yönetimi |
| `OfflineAnalyze.tsx` | `/offline-analyze` | Kaydedilmiş video dosyasını çevrimdışı analiz et |

### LLM Sağlayıcı Seçimi

Backend birden fazla LLM sağlayıcısını destekler. `appsettings.json` içindeki `Llm__Provider` ile seçilir:

| Sağlayıcı | `Llm__Provider` değeri | Not |
|-----------|----------------------|-----|
| OpenAI | `OpenAI` | Varsayılan. `gpt-4o` / `gpt-4o-mini` |
| Anthropic | `Anthropic` | Claude modelleri |
| Ollama | `Ollama` | Yerel, internet bağlantısı gerektirmez |

---

## Veritabanı Tabloları (Çekirdek)

| Tablo | Açıklama |
|-------|----------|
| `Sessions` | Mülakat oturumları, metadata, durum |
| `Questions` | Oturuma bağlı sorular ve yanıtları |
| `MetricEvents` | Ham görüntü metrik olayları (göz teması, duruş vb.) |
| `TranscriptSegments` | Konuşma-yazı dönüşümü parçaları |
| `ScoreCards` | Finalize sonrası hesaplanan skorlar |
| `FeedbackItems` | LLM coaching çıktısı maddeleri |

---

## Kimlik Doğrulama

Sistem JWT Bearer token ile çalışır. Tüm `/api/*` endpoint'leri (sağlık kontrolleri hariç) token gerektirir.

```
POST /api/auth/register   → { email, password, name } → { token, expiresAt }
POST /api/auth/login      → { email, password }        → { token, expiresAt }
```

Token, her istekte `Authorization: Bearer <token>` başlığı olarak gönderilir.

**İlk admin hesabı:** `appsettings.json` veya ortam değişkenleri ile `Auth__SeedAdminEmail` ve `Auth__SeedAdminPassword` doldurulursa başlangıçta admin oluşturulur.

---

## Uçtan Uca Pipeline

```
1. Oturum oluştur      POST /api/sessions
2. Metrik aktar         POST /api/sessions/{id}/events/batch        (tekrar tekrar)
3. Transcript aktar     POST /api/sessions/{id}/transcript/batch    (soru bitişinde)
4. Finalize             POST /api/sessions/{id}/finalize
5. Sonuçları tüket:
     GET  /api/reports/{id}
     GET  /api/sessions/{id}/evidence-summary
     POST /api/sessions/{id}/llm/coach
```

### Video Kaydı

Frontend, `MediaRecorder` API ile `video/webm;codecs=vp8,opus` formatında webcam görüntüsü + sesi kaydeder. Her sorunun yanıtı ayrı bir webm dosyası olarak `POST /api/sessions/{id}/questions/{order}/audio` ile yüklenir. Backend bu dosyayı konuşma servisine iletir ve transkript alır.

### Görüntü Analizi

`MediaPipe Face Mesh` ve `Pose` modelleri, mülakat boyunca sürekli çalışır. Her ~500ms'de bir toplu metrik olayı (`vision_metrics_v1`) backend'e gönderilir.

### Konuşma Servisi

Streaming (WebSocket) ASR kaldırılmıştır. Yalnızca **batch HTTP** transkripsiyon kullanılmaktadır:

```
POST http://speech-service:8000/transcribe
Content-Type: multipart/form-data
Body: audio dosyası (video/webm)
```

Servis hazırlık durumu için:
```
GET http://speech-service:8000/health
```

---

## Puanlama Profilleri

Finalize aşamasında metrikler, seçilen profile göre ağırlıklandırılarak skora dönüştürülür.

| Profil | Kullanım Alanı | Öne Çıkan Ağırlık |
|--------|---------------|------------------|
| `general` | Genel mülakat (varsayılan) | Dengeli |
| `technical` | Teknik pozisyon | Konuşma hızı + filler sözcük |
| `hr` | İK / davranışsal | Göz teması + duruş |

Aktif profil `POST /api/sessions/{id}/scoring/profile` ile değiştirilir.
Tüm profiller `GET /api/scoring/profiles` ile listelenir.

---

## Şema Örnekleri

### 1) Metrik Olayları Toplu Aktarımı (`vision_metrics_v1`)

`POST /api/sessions/{sessionId}/events/batch`

```json
[
  {
    "clientEventId": "11111111-1111-1111-1111-111111111111",
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
```

### 2) Transcript Toplu Aktarımı

`POST /api/sessions/{sessionId}/transcript/batch`

```json
[
  {
    "clientSegmentId": "22222222-2222-2222-2222-222222222222",
    "startMs": 1200,
    "endMs": 3600,
    "text": "Merhaba, başlamaya hazırım.",
    "confidence": 0.93
  }
]
```

### 3) Finalize Yanıtı

`POST /api/sessions/{sessionId}/finalize`

```json
{
  "sessionId": "33333333-3333-3333-3333-333333333333",
  "scoreCard": {
    "eyeContactScore": 78,
    "speakingRateScore": 81,
    "fillerScore": 74,
    "postureScore": 76,
    "overallScore": 77
  },
  "patterns": [
    {
      "type": "audio",
      "startMs": 5000,
      "endMs": 9000,
      "severity": 3,
      "evidence": "Bu aralıkta sık dolgu sözcük kullanımı."
    }
  ],
  "derivedFeatureCount": 12
}
```

> `speakingRateScore` ve `fillerScore` transcript verisine dayanır; konuşma servisi devrede değilse bu değerler sıfır olabilir.

### 4) LLM Koçluk Şeması

`POST /api/sessions/{sessionId}/llm/coach`

```json
{
  "rubric": {
    "technical_correctness": 72,
    "depth": 65,
    "structure": 80,
    "clarity": 78,
    "confidence": 70
  },
  "overall": 73,
  "feedback": [
    {
      "category": "vision",
      "severity": 2,
      "title": "Göz teması zayıf",
      "evidence": "Yanıt süresinin %38'inde kameraya bakılmadı.",
      "time_range_ms": [5000, 9000],
      "suggestion": "Cevap verirken kameraya odaklanmayı deneyin.",
      "example_phrase": "Kameraya bakarken 'Bu konuda şunu düşünüyorum...' diyebilirsiniz."
    }
  ],
  "drills": [
    {
      "title": "Göz teması egzersizi",
      "steps": ["Aynaya bakarak 2 dakika konuşun", "Kayıt alarak izleyin"],
      "duration_min": 5
    }
  ]
}
```

---

## Oturum Tekrar Oynatma (Debug / QA)

Replay endpoint'leri, oturumları deterministik olarak yeniden çalıştırmak ve pipeline çıktılarını karşılaştırmak için kullanılır.

> **Not:** Import ve Run endpoint'leri `AdminOnly` policy gerektirir. İsteklerde `Authorization: Bearer <admin-token>` başlığı zorunludur.

### Dışa Aktar
```bash
curl -H "Authorization: Bearer <token>" \
  http://localhost:8080/api/sessions/<sessionId>/replay/export \
  -o replay.json
```

### İçe Aktar (Admin)
```bash
curl -X POST \
  -H "Authorization: Bearer <admin-token>" \
  -H "Content-Type: application/json" \
  --data @replay.json \
  http://localhost:8080/api/sessions/replay/import
```

### Tekrar Oynat (Admin)
```bash
curl -X POST \
  -H "Authorization: Bearer <admin-token>" \
  -H "Content-Type: application/json" \
  -d '{"speed":1.0}' \
  http://localhost:8080/api/sessions/<newSessionId>/replay/run
```

### Tipik Kullanım Senaryoları
- Tam olay + transcript girdisiyle bir hatayı yeniden üret.
- Backend değişikliklerinden önce ve sonra skorlama/koçluk çıktılarını karşılaştır.
- Regresyon testi için deterministik QA fixture'ları oluştur.
