# 🔍 Yapay Zeka Mülakat Platformu — Proje İnceleme & Güncelleme Raporu

> **Repo:** [HakaniOzdogan/Yapay_Zeka_Mulakat](https://github.com/HakaniOzdogan/Yapay_Zeka_Mulakat)
> **İnceleme Tarihi:** 16 Mayıs 2026

---

## 📋 İçindekiler

1. [Özet Tablo](#özet-tablo)
2. [Kritik Eksiklikler](#-kritik-eksiklikler)
3. [Önemli Eksiklikler](#-önemli-eksiklikler)
4. [İyileştirme Önerileri](#-iyileştirme-önerileri)
5. [Öncelik Sırası & Yol Haritası](#-öncelik-sırası--yol-haritası)

---

## 📊 Özet Tablo

| Alan | Durum | Öncelik |
|---|---|---|
| Proje Dokümantasyonu | ✅ Mükemmel | — |
| README.md | ❌ Güncellenmeli | 🔴 Kritik |
| `.env.example` | ❌ Eksik | 🔴 Kritik |
| Git History | ⚠️ Yetersiz (1 commit) | 🔴 Kritik |
| Python Servisi (`python-service/`) | ❌ Eksik | 🔴 Kritik |
| Kaynak Kodlar (`src/`) | ⚠️ İçerik belirsiz | 🟠 Önemli |
| `prisma.config.ts` çakışması | ⚠️ Gözden geçirilmeli | 🟠 Önemli |
| `public/models/` (face-api.js) | ❌ Eksik | 🟠 Önemli |
| Test Altyapısı | ❌ Eksik | 🟠 Önemli |
| `docker-compose.yml` | ❌ Eksik | 🟠 Önemli |
| `prisma/seed.ts` | ❌ Eksik | 🟡 İyileştirme |
| `next.config.ts` uyumu | ⚠️ Kontrol edilmeli | 🟡 İyileştirme |
| `package.json` kütüphane uyumu | ⚠️ Kontrol edilmeli | 🟡 İyileştirme |
| Güvenlik (API key yönetimi) | ⚠️ Gözden geçirilmeli | 🟡 İyileştirme |
| Ödeme sistemi implementasyonu | ⚠️ Muhtemelen eksik | 🟡 İyileştirme |

---

## 🔴 Kritik Eksiklikler

### 1. README.md Tamamen Varsayılan (create-next-app Çıktısı)

**Sorun:** `README.md` hâlâ Next.js'in default README'sidir. Projeye özel hiçbir bilgi içermiyor. `AI_MULAKAT_PLATFORMU_PROJE_DOKUMANTASYONU.md` adında kapsamlı bir dokümantasyon mevcut olmasına rağmen bu iki dosya birbiriyle bağlantılandırılmamış.

**Yapılması Gereken:**
- `README.md`'i projeyi tanıtan, kurulum adımlarını anlatan ve dokümantasyon dosyasına yönlendiren özel bir içerikle güncelle.
- Minimum içerik: proje açıklaması, teknoloji stack özeti, kurulum adımları, environment variable listesi, dokümantasyona link.

---

### 2. `.env.example` Dosyası Yok

**Sorun:** Dokümantasyonda 20'yi aşkın environment variable tanımlanmış (veritabanı, OAuth, OpenAI, Stripe, AWS, vb.) ancak repoda `.env.example` şablon dosyası bulunmuyor. Projeyi klonlayan herhangi biri neye ihtiyaç duyduğunu bilemez.

**Yapılması Gereken:**
- Tüm değişkenleri boş değerlerle içeren `.env.example` dosyası oluştur.
- Her değişkenin yanına kısa bir yorum satırı ekle.

```dotenv
# Örnek .env.example içeriği

# Veritabanı
DATABASE_URL="postgresql://user:pass@localhost:5432/interview_db"
MONGODB_URI="mongodb://localhost:27017/interview_logs"
REDIS_URL="redis://localhost:6379"

# NextAuth
NEXTAUTH_URL="http://localhost:3000"
NEXTAUTH_SECRET=""   # min 32 karakter, openssl rand -base64 32

# OAuth
GOOGLE_CLIENT_ID=""
GOOGLE_CLIENT_SECRET=""

# OpenAI
OPENAI_API_KEY=""    # sk-...

# ElevenLabs (TTS)
ELEVENLABS_API_KEY=""

# Harici API'ler
RAPIDAPI_KEY=""
APIFY_API_TOKEN=""

# AWS S3 / Cloudflare R2
AWS_ACCESS_KEY_ID=""
AWS_SECRET_ACCESS_KEY=""
AWS_REGION="eu-central-1"
AWS_S3_BUCKET="interview-videos"

# Ödeme
STRIPE_PUBLIC_KEY=""   # pk_test_...
STRIPE_SECRET_KEY=""   # sk_test_...
STRIPE_WEBHOOK_SECRET=""

# Python Microservice
PYTHON_SERVICE_URL="http://localhost:8000"

# Monitoring
SENTRY_DSN=""
LOGROCKET_APP_ID=""
```

---

### 3. Sadece 1 Commit Var

**Sorun:** Tüm proje tek bir commit ile push edilmiş. Bu durum:
- Git geçmişini anlamsız kılıyor.
- Hangi özelliğin ne zaman eklendiğini takip etmeyi imkânsızlaştırıyor.
- Takım çalışmasını güçleştiriyor.
- Code review süreçlerini zorlaştırıyor.

**Yapılması Gereken:**
- Anlamlı commit mesajları kullan (örnek: `feat(auth): add Google OAuth`, `fix(interview): correct question generation prompt`).
- Commit convention olarak [Conventional Commits](https://www.conventionalcommits.org/) standardını benimse:
  - `feat` → yeni özellik
  - `fix` → hata düzeltme
  - `docs` → dokümantasyon
  - `refactor` → yeniden yapılandırma
  - `test` → test ekleme
  - `chore` → bakım işleri

---

### 4. `python-service/` Klasörü Yok

**Sorun:** Dokümantasyon; video analizi, ses analizi, ML model çıkarımı ve gerçek zamanlı WebSocket akışı için kapsamlı bir Python FastAPI microservice tanımlıyor. Ancak repoda bu klasör hiç bulunmuyor. Bu eksiklik projenin en kritik özelliklerini (yüz ifadesi analizi, ses özellikleri, duygu tespiti) işlevsiz bırakıyor.

**Yapılması Gereken:**
- Aşağıdaki yapıyla `python-service/` klasörünü oluştur:

```
python-service/
├── main.py
├── requirements.txt
├── Dockerfile
├── routers/
│   ├── video_analysis.py
│   ├── audio_analysis.py
│   └── transcription.py
├── services/
│   ├── face_detection.py
│   ├── emotion_recognition.py
│   ├── audio_features.py
│   └── ml_models.py
├── models/          # ML model ağırlıkları
└── utils/
    └── preprocessing.py
```

- `requirements.txt` minimum içerik:

```
fastapi>=0.104.0
uvicorn[standard]>=0.24.0
openai-whisper
librosa
numpy
torch
mediapipe
face-recognition
python-multipart
websockets
```

---

## 🟠 Önemli Eksiklikler

### 5. `src/` Klasörü İçeriği Belirsiz

**Sorun:** `src/` klasörü repoda listeleniyor ancak dokümantasyonda tarif edilen detaylı yapının (`app/`, `components/`, `lib/`, `hooks/`, `store/`, `types/`) gerçekten mevcut olup olmadığı doğrulanamıyor.

**Yapılması Gereken:**
- Özellikle şu kritik dizinlerin var olduğunu kontrol et:
  - `src/app/api/interview/` — mülakat API route'ları
  - `src/components/interview/live/` — canlı mülakat bileşenleri
  - `src/lib/ai/` — OpenAI entegrasyonu
  - `src/lib/video/` — video kayıt yöneticisi
  - `src/store/` — Zustand store'ları
- Eksik olanları aşamalı olarak ekle.

---

### 6. `prisma.config.ts` ile `prisma/schema.prisma` Çakışması

**Sorun:** Repoda hem `prisma/schema.prisma` hem de kök dizinde `prisma.config.ts` bulunuyor. Prisma'nın standart yapısında ayrı bir `prisma.config.ts` beklenmez; tüm konfigürasyon `schema.prisma` içindeki `generator` ve `datasource` bloklarında yapılır.

**Yapılması Gereken:**
- `prisma.config.ts`'in ne işe yaradığını belgele veya içeriği `schema.prisma`'ya taşıyarak dosyayı sil.
- Eğer Prisma'nın yeni özelliklerinden biri için gerekliyse (örn. `prisma.config.ts` desteği sunan bir sürüm), hangi Prisma sürümüne ihtiyaç duyulduğunu `README.md`'de belirt.

---

### 7. `public/models/` Klasörü Eksik

**Sorun:** Gerçek zamanlı yüz ve duygu analizi için face-api.js kütüphanesi kullanılıyor. Bu kütüphane çalışabilmek için `public/models/` altında model ağırlık dosyalarına ihtiyaç duyuyor. Bu dosyalar olmadan video analizi tamamen işlevsiz kalır.

**Yapılması Gereken:**
- Model dosyaları büyük olduğu için repoya direkt ekleme yerine indirme talimatını belgele:

```bash
# public/models/ dizinine face-api.js modellerini indir
mkdir -p public/models
cd public/models

# Gerekli modeller:
# - tiny_face_detector
# - face_landmark_68
# - face_recognition
# - face_expression

npx face-api.js-models
# veya manuel olarak:
# https://github.com/justadudewhohacks/face-api.js/tree/master/weights
```

- `.gitignore`'a `public/models/` ekle ama `README.md`'de nasıl kurulacağını açıkla.

---

### 8. Test Altyapısı Tamamen Eksik

**Sorun:** Dokümantasyon unit, integration ve e2e (Playwright) testlerini detaylı şekilde tanımlıyor, kod örnekleri bile içeriyor. Ancak repoda tek bir test dosyası mevcut değil.

**Yapılması Gereken:**
- Temel test altyapısını kur:

```bash
# Jest + Testing Library kurulumu
npm install -D jest @testing-library/react @testing-library/jest-dom jest-environment-jsdom

# Playwright kurulumu
npm install -D @playwright/test
npx playwright install
```

- Minimum olarak şu test dosyalarını oluştur:
  - `__tests__/unit/interview.test.ts`
  - `__tests__/integration/api.test.ts`
  - `e2e/interview-flow.spec.ts`

- `package.json`'a test script'lerini ekle:

```json
{
  "scripts": {
    "test": "jest",
    "test:watch": "jest --watch",
    "test:e2e": "playwright test",
    "test:coverage": "jest --coverage"
  }
}
```

---

### 9. `docker-compose.yml` Eksik

**Sorun:** Dokümantasyon hem development hem production için Docker Compose konfigürasyonu içeriyor ancak bu dosyalar repoda bulunmuyor. PostgreSQL, Redis, MongoDB ve Python servisi olmadan proje lokal ortamda çalıştırılamaz.

**Yapılması Gereken:**
- `docker-compose.yml` (development) dosyası oluştur:

```yaml
version: '3.8'

services:
  postgres:
    image: postgres:15-alpine
    ports:
      - "5432:5432"
    environment:
      POSTGRES_DB: interview_db
      POSTGRES_USER: user
      POSTGRES_PASSWORD: password
    volumes:
      - postgres_data:/var/lib/postgresql/data

  redis:
    image: redis:7-alpine
    ports:
      - "6379:6379"
    volumes:
      - redis_data:/data

  mongodb:
    image: mongo:6
    ports:
      - "27017:27017"
    volumes:
      - mongo_data:/data/db

volumes:
  postgres_data:
  redis_data:
  mongo_data:
```

---

## 🟡 İyileştirme Önerileri

### 10. `prisma/seed.ts` Yok

**Sorun:** Dokümantasyonda `npm run db:seed` komutundan bahsediliyor ve `package.json`'da `db:seed` script'i olması gerekiyor. Ancak `prisma/seed.ts` dosyası repoda yok.

**Yapılması Gereken:**
- `prisma/seed.ts` oluştur. Minimum içerik: test kullanıcısı, örnek başarım (achievement) kayıtları ve demo şirket profilleri.
- `package.json`'a ekle:

```json
{
  "prisma": {
    "seed": "ts-node --compiler-options {\"module\":\"CommonJS\"} prisma/seed.ts"
  }
}
```

---

### 11. `next.config.ts` Sürüm Uyumu

**Sorun:** `next.config.ts` (TypeScript) kullanımı Next.js 15+ ile gelen yeni bir özelliktir. Eğer proje Next.js 14 kullanıyorsa (`"Next.js 14 (App Router)"` — dokümantasyondan) bu dosya sorun çıkarabilir.

**Yapılması Gereken:**
- `package.json`'daki Next.js sürümünü kontrol et.
- Next.js 14 kullanılıyorsa `next.config.ts`'i `next.config.js`'e dönüştür veya Next.js'i 15'e güncelle.

---

### 12. `package.json` Kütüphane Uyumu

**Sorun:** Dokümantasyon aşağıdaki kütüphanelerin kullanıldığını belirtiyor. Bunların `package.json`'da gerçekten tanımlı olup olmadığı doğrulanmalı:

| Kütüphane | Amaç |
|---|---|
| `zustand` | State management |
| `@tanstack/react-query` | Server state |
| `socket.io-client` | Real-time |
| `framer-motion` | Animasyonlar |
| `recharts` | Grafikler |
| `zod` | Validation |
| `react-hook-form` | Form yönetimi |
| `face-api.js` | Duygu analizi |
| `@mediapipe/face_mesh` | Yüz landmark |
| `@mediapipe/pose` | Duruş analizi |
| `bcryptjs` | Şifre hashleme |
| `@auth/prisma-adapter` | NextAuth adapter |

**Yapılması Gereken:**
- Her birinin `package.json`'da olduğunu kontrol et, eksikleri kur:

```bash
npm install zustand @tanstack/react-query socket.io-client framer-motion recharts zod react-hook-form face-api.js @mediapipe/face_mesh bcryptjs @auth/prisma-adapter
```

---

### 13. Güvenlik: API Key Yönetimi

**Sorun:** OpenAI, ElevenLabs, RapidAPI, Stripe gibi hassas servisler kullanılıyor. Yanlışlıkla `NEXT_PUBLIC_` prefix'i eklenen herhangi bir gizli key tarayıcıya açık hale gelir.

**Yapılması Gereken:**
- Tüm gizli key'lerin `NEXT_PUBLIC_` prefix'i **olmadığından** emin ol.
- Sadece şunlar `NEXT_PUBLIC_` olabilir: `NEXT_PUBLIC_STRIPE_PUBLIC_KEY`, `NEXT_PUBLIC_SENTRY_DSN`
- Tüm OpenAI, AWS, Stripe Secret key'leri sadece server-side API route'larda kullanılmalı.
- Kod içinde şu kontrolü uygula:

```typescript
// ❌ Yanlış — key tarayıcıya sızar
const key = process.env.NEXT_PUBLIC_OPENAI_API_KEY;

// ✅ Doğru — sadece server'da çalışır
const key = process.env.OPENAI_API_KEY;
```

---

### 14. Ödeme Sistemi Implementasyonu Belirsiz

**Sorun:** Dokümantasyon Stripe ve İyzico entegrasyonunu, abonelik katmanlarını ve webhook handler'larını detaylı anlatıyor. Ancak `src/` altında payment ile ilgili dosyaların (API route'lar, webhook handler, `SubscriptionTier` middleware) mevcut olup olmadığı belirsiz.

**Yapılması Gereken:**
- Şu dosyaların varlığını kontrol et:
  - `src/app/api/payment/create-checkout/route.ts`
  - `src/app/api/payment/webhook/route.ts`
  - `src/lib/external/stripe.ts`
  - `src/lib/subscription.ts` — feature gate kontrolü
- Eksikleri aşamalı olarak implement et.

---

## 🗺️ Öncelik Sırası & Yol Haritası

### Aşama 1 — Temel Kurulum (1-2 Gün)

- [ ] `README.md`'i projeye özel içerikle güncelle
- [ ] `.env.example` dosyasını oluştur
- [ ] `docker-compose.yml` dosyasını ekle
- [ ] `prisma/seed.ts` oluştur

### Aşama 2 — Eksik Yapılar (3-5 Gün)

- [ ] `python-service/` klasörünü temel yapısıyla oluştur
- [ ] `public/models/` indirme talimatını belgeye al
- [ ] `prisma.config.ts` meselesini çöz
- [ ] `package.json` kütüphane eksiklerini tamamla

### Aşama 3 — Kalite & Güvenlik (Devam Eden)

- [ ] Test altyapısını kur (Jest + Playwright)
- [ ] API key güvenlik denetimi yap
- [ ] `next.config.ts` sürüm uyumunu kontrol et
- [ ] Ödeme sistemi implementasyonunu tamamla
- [ ] Anlamlı commit geçmişi oluşturmaya başla

---

*Bu rapor, projenin GitHub reposu ve `AI_MULAKAT_PLATFORMU_PROJE_DOKUMANTASYONU.md` dosyası incelenerek hazırlanmıştır.*
