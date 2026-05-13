# 11 — Frontend Mimari Rehberi

## Teknoloji Stack

- **React 18** — UI framework
- **TypeScript** — Tip güvenliği
- **Vite** — Build ve dev server
- **React Router** — Sayfa yönlendirme
- **MediaPipe** — Görüntü analizi (WASM)

## Dizin Yapısı

```
src/frontend/src/
├── pages/                    # Sayfa bileşenleri
│   ├── Home.tsx              # Ana sayfa, mülakat başlatma
│   ├── AuthPage.tsx          # Kayıt ve giriş
│   ├── InterviewSession.tsx  # Mülakat oturumu (ana ekran)
│   ├── Report.tsx            # Rapor görüntüleme
│   ├── ReportsList.tsx       # Geçmiş raporlar listesi
│   ├── AdminPage.tsx         # Admin paneli
│   └── OfflineAnalyze.tsx    # Offline analiz
├── components/               # Tekrar kullanılır bileşenler
│   ├── VideoCanvas.tsx       # Kamera + overlay render
│   ├── LiveHints.tsx         # Canlı coaching ipuçları
│   ├── TopNav.tsx            # Üst navigasyon
│   └── TranscriptModal.tsx   # Transkript popup
├── services/                 # İş mantığı servisleri
│   ├── ApiService.ts         # REST API çağrıları
│   ├── MediaPipeService.ts   # MediaPipe init ve detect
│   ├── MetricsComputer.ts    # Metrik hesaplama
│   ├── AudioAnalyzer.ts      # Ses seviyesi analizi
│   ├── CoachingHints.ts      # Canlı ipucu üretimi
│   ├── SessionTransport.ts   # Backend'e metrik gönderimi
│   └── speechRecognitionService.ts
├── speech/                   # ASR modülü
│   └── streamingAsr.ts       # WebSocket streaming ASR
├── vision/                   # Görüntü analiz modülleri
│   ├── features.ts           # Metrik hesaplama fonksiyonları
│   ├── buffer.ts             # Rolling buffer
│   └── types.ts              # Vision tipleri
├── auth/                     # Kimlik doğrulama
│   ├── AuthContext.tsx        # Auth React context
│   ├── ProtectedRoute.tsx     # Korumalı rota wrapper
│   ├── AdminRoute.tsx         # Admin-only rota wrapper
│   └── authStorage.ts        # Token saklama
├── styles/                   # CSS dosyaları
│   ├── index.css             # Global stiller
│   ├── pages.css             # Sayfa-spesifik stiller
│   └── admin-batch.css       # Admin paneli stilleri
├── utils/
│   └── download.ts           # Dosya indirme helper
├── App.tsx                   # Root bileşen + router
└── main.tsx                  # Entry point
```

## Sayfa Akışı

```
AuthPage (Giriş/Kayıt)
    │
    ▼
Home (Mülakat Ayarları)
    │
    ▼
InterviewSession (Mülakat Oturumu)
    │
    ▼
Report (Rapor Görüntüleme)

Yan sayfa: ReportsList (Geçmiş Raporlar)
Yan sayfa: AdminPage (Admin Paneli)
```

## Ana Bileşenler

### InterviewSession.tsx

En büyük ve karmaşık bileşen (~1400 satır). Sorumlulukları:

- Kamera ve mikrofon izni alma
- MediaPipe başlatma ve kalibrasyon
- Her frame için vision metrik hesaplama
- ASR bağlantısı kurma ve yönetme
- Metrik ve transkript batch gönderimi
- Soru akışı yönetimi
- Video kayıt ve upload

### Report.tsx

Rapor görüntüleme bileşeni (~600 satır). Sorumlulukları:

- Rapor verisini API'den çekme
- Skor kartları ve metrik grafikleri render
- Soru bazlı video oynatma
- Transkript gösterimi
- AI coaching isteme ve gösterme
- Puanlama profili değiştirme
- Dışa aktarma (JSON, Markdown, Print)

### VideoCanvas.tsx

Kamera görüntüsü ve overlay render bileşeni. Canvas üzerine:
- Kamera frame çizer
- MediaPipe landmark'larını overlay olarak çizer (opsiyonel)
- Yüz mesh, pose iskelet ve metrik değerlerini gösterir

## API Service

`ApiService.ts` tüm backend iletişimini yönetir. Temel metodlar:

```typescript
// Oturum yönetimi
createSession(role, language)
getSession(sessionId)
finalizeSession(sessionId)

// Metrik ve transkript
pushMetricEvents(sessionId, events)
pushTranscriptBatch(sessionId, segments)

// Rapor
getReport(sessionId)
getLlmCoaching(sessionId)
generateLlmCoaching(sessionId)

// Dışa aktarma
downloadReportExportJson(sessionId)
downloadReportExportMarkdown(sessionId)

// Puanlama profili
getScoringProfiles()
previewScoringProfile(sessionId, profileName)
setScoringProfile(sessionId, profileName)

// Auth
login(email, password)
register(email, password)
```

## Kimlik Doğrulama

JWT tabanlı kimlik doğrulama React Context ile yönetilir:

- `AuthContext.tsx` — Login/logout state, token yönetimi
- `ProtectedRoute.tsx` — Giriş yapmamış kullanıcıları AuthPage'e yönlendirir
- `AdminRoute.tsx` — Admin olmayan kullanıcıları ana sayfaya yönlendirir
- Token localStorage'da saklanır, her API isteğinde header'a eklenir

## Ortam Değişkenleri

```env
VITE_API_URL=http://localhost:8080/api    # Backend API adresi
```

Vite'da ortam değişkenleri `import.meta.env.VITE_*` ile erişilir.
