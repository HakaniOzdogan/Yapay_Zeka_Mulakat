# CHANGELOG — Tasarım Kararları ve Değişiklikler

Bu dosya, orijinal `AI_MULAKAT_PLATFORMU_PROJE_DOKUMANTASYONU.md` dosyasından yapılan tüm değişiklikleri ve bu değişikliklerin gerekçelerini belgelemektedir.

---

## [2.0.0] — Mayıs 2025 (Aktif Tasarım Revizyonu)

### Değiştirilen: STT Sistemi (Kritik)

**Eski:** Whisper API üzerinden 3 saniyede bir chunk gönderilerek real-time transkripsiyon yapılıyordu. Live transcript mülakat ekranında gösteriliyordu.

**Yeni:** ElevenLabs Scribe v2 Batch kullanılıyor. Kullanıcı cevabı bitirince tüm ses dosyası bir kerede gönderilir. Live transcript kaldırıldı; transkript yalnızca raporda gösterilir.

**Gerekçe:**
- Whisper API Türkçe WER: ~%7.6 → Scribe v2: ≤%5
- 3 sn'lik chunk'lar cümle ortasında kesildiği için bağlam kaybı oluşuyordu
- Live transcript UX değeri düşük; kullanıcı konuşmasını okuyarak konuşamaz
- Scribe v2 Batch'te keyterm prompting özelliği var: pozisyona özel teknik terimler tanımlanabiliyor (max 1000 terim)
- Maliyet: Scribe v2 $0.0037/dk, OpenAI Whisper API $0.006/dk

**Değiştirilen dosyalar:**
- `src/lib/ai/scribe.ts` — yeni Scribe v2 entegrasyonu
- `src/lib/ai/keyterms.ts` — pozisyon bazlı keyterm haritası
- `src/lib/ai/transcription-manager.ts` — async transkripsiyon yönetimi
- `prisma/schema.prisma` — `TranscriptionStatus` enum, `scribeJobId` alanı eklendi
- `src/app/api/interview/[id]/submit-answer/route.ts` — clip upload + async transkripsiyon

---

### Eklenen: Video Kayıt Sistemi (Yeni Özellik)

**Eski:** Yalnızca ses kaydı tutuluyordu.

**Yeni:** Ekran (tüm ekran) ve webcam aynı anda kaydedilir. Canvas compositor ile iki stream birleştirilir ve picture-in-picture (PiP) formatında tek bir video dosyası oluşturulur. İki paralel kayıt çalışır: mülakat boyunca sürekli kayıt ve her soru için ayrı clip.

**Teknik kararlar:**
- `getDisplayMedia({ displaySurface: 'monitor' })` — tüm ekranı öner
- `getUserMedia({ video, audio })` — webcam + mikrofon
- `requestAnimationFrame` döngüsüyle canvas compositor (her frame ekran + PiP)
- Webcam PiP: sağ alt köşe, 200×150 px, 12 px yuvarlak köşe
- Ses track'i ekran stream'den değil kamera stream'den alınır
- `canvas.captureStream(30)` → `MediaRecorder` (vp9+opus codec)
- Soru clip'leri cevap bitince hemen S3'e yüklenir (küçük dosyalar)
- Tam kayıt mülakat bitince yüklenir

**Sekme değişimi:** Kayıt devam eder, kullanıcıya toast bildirimi gösterilir. Raporda sekme değişim sayısı not olarak yer alır.

**Yeni dosyalar:**
- `src/lib/video/dual-stream-recorder.ts` — `DualStreamRecorder` sınıfı
- `src/lib/video/upload.ts` — S3 presigned URL upload
- `src/hooks/use-dual-stream-recorder.ts` — React hook
- `src/components/interview/CameraPreview.tsx`
- `src/components/interview/TabSwitchWarning.tsx`

---

### Kaldırılan: MongoDB (Mimari Basitleştirme)

**Eski:** PostgreSQL + MongoDB + Redis üçlü veritabanı mimarisi planlanmıştı.

**Yeni:** PostgreSQL + Redis. MongoDB kaldırıldı.

**Gerekçe:**
- MVP için üç veritabanı gereksiz karmaşıklık
- PostgreSQL JSONB kolonları MongoDB'nin sağladığı esnekliği sunar
- Üç farklı client ile veri tutarlılığını sağlamak çok maliyetli
- Prisma ile tek ORM yeterli

**Değiştirilen:** `prisma/schema.prisma` — analiz verileri, şirket verileri ve session verileri PostgreSQL'e taşındı. Redis yalnızca rate limiting ve cache için kullanılıyor.

---

### Kaldırılan: Socket.io (Uyumluluk Sorunu)

**Eski:** Real-time bildirimler için Socket.io planlanmıştı.

**Yeni:** Socket.io MVP'den çıkarıldı.

**Gerekçe:**
- Next.js 14 App Router, Socket.io ile doğrudan uyumsuz
- Custom server gerektirir, bu Vercel deploy'unu karmaşıklaştırır
- MVP'de real-time özellik zorunlu değil
- İleride: Pusher veya Ably ile entegre edilebilir (yönetilen WebSocket servisleri, App Router uyumlu)

---

### Eklenen: Rate Limiting (Güvenlik)

**Eski:** API route'larında rate limiting yoktu.

**Yeni:** Upstash Redis + `@upstash/ratelimit` ile tüm endpoint gruplarında rate limiting.

| Grup | Limit | Pencere |
|------|-------|---------|
| Genel | 100 istek | 15 dakika |
| AI endpoint'leri | 10 istek | 1 dakika |
| Auth | 5 istek | 15 dakika |
| Upload | 20 istek | 1 saat |

**Yeni dosyalar:**
- `src/lib/api/rate-limit.ts`

---

### Eklenen: withAuth Wrapper (Güvenlik)

**Eski:** Her API route'unda manuel session kontrolü yapılıyordu (tutarsız).

**Yeni:** `withAuth` higher-order function ile tüm korumalı route'lar sarılır.

**Yeni dosyalar:**
- `src/lib/api/auth-guard.ts`

---

### Eklenen: Prisma Client Singleton (Performans)

**Eski:** Her modül kendi `PrismaClient` instance'ını oluşturuyordu.

**Yeni:** `src/lib/prisma.ts` global singleton pattern.

**Gerekçe:** Next.js development modunda hot-reload her seferinde yeni veritabanı bağlantısı açıyor. Singleton bu sorunu önler.

---

### Eklenen: TypeScript Strict Mode

**Eski:** `tsconfig.json`'da strict mode durumu belirsizdi.

**Yeni:** `"strict": true`, `"noImplicitAny": true`, `"noUnusedLocals": true` zorunlu.

---

### Eklenen: Error Boundaries

**Eski:** `error.tsx` ve `not-found.tsx` dosyaları yoktu.

**Yeni:**
- `src/app/error.tsx` — global React error boundary
- `src/app/not-found.tsx` — 404 sayfası
- `src/app/interview/[id]/error.tsx` — mülakat sayfası hata yönetimi

---

### Eklenen: GitHub Actions CI

**Eski:** CI pipeline yoktu.

**Yeni:** `.github/workflows/ci.yml` — TypeScript tip kontrolü, ESLint, Prisma validate, testler.

---

### Eklenen: docker-compose.yml

**Eski:** Yerel geliştirme için Docker konfigürasyonu yoktu.

**Yeni:** `docker-compose.yml` ile PostgreSQL + Redis tek komutla ayağa kalkar.

---

### Düzeltilen: src/ ve app/ Klasör Çakışması

**Eski:** `src/` klasörü ile root-level `app/` klasörü aynı anda mevcuttu.

**Yeni:** Yalnızca `src/app/` kullanılır. Root'ta `app/` klasörü bulunmaz.

**Gerekçe:** Next.js 14'te ya `src/app/` ya da root `app/` kullanılır, ikisi birden değil. İkisi bir arada olunca router belirsizliği ve build hataları oluşur.

---

### Güncellenen: README.md

**Eski:** `create-next-app` şablonundan gelen varsayılan README.

**Yeni:** Projeye özel kurulum adımları, tech stack tablosu, dokümantasyon indeksi, klasör yapısı.

---

## Önümüzdeki Adımlar (Backlog)

Aşağıdaki özellikler MVP sonrasına bırakıldı:

- [ ] Real-time bildirimler (Pusher veya Ably ile)
- [ ] LinkedIn OAuth eklenmesi
- [ ] CV parse ve otomatik pozisyon tespiti
- [ ] Grup mülakat modu (birden fazla kullanıcı)
- [ ] Şirket özel mülakat paketi (B2B)
- [ ] Mobil uygulama (React Native)
- [ ] Iyzico entegrasyonu (Stripe'a alternatif TR ödeme)
- [ ] KVKK kapsamlı uyumluluk raporu ve onay akışı
