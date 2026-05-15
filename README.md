# AI Mülakat Hazırlık Platformu

Kullanıcıların gerçek bir mülakat deneyimi yaşayarak, ekran + webcam kaydı, ses analizi ve AI destekli interaktif sorularla mülakata hazırlanmasını sağlayan web platformu.

## Temel Özellikler

- **Gerçekçi mülakat simülasyonu:** Şirket ve pozisyon bazlı, adaptif AI soru üretimi (GPT-4 Turbo)
- **Ekran + webcam kaydı (PiP):** Canvas compositor ile tüm ekran + webcam aynı anda kaydedilir
- **Yüksek doğrulukta transkripsiyon:** ElevenLabs Scribe v2 Batch — Türkçe + İngilizce, ≤%5 WER
- **Video analizi:** MediaPipe Face Mesh ile yüz ifadesi, göz teması, jest-mimik analizi
- **Ses analizi:** Python FastAPI + librosa ile konuşma hızı, tonlama, dolgu kelime tespiti
- **Detaylı raporlama:** Soru bazlı video clip'ler, transkript, AI geri bildirim, gelişim takibi
- **Monetizasyon:** Freemium model (Free / Basic / Pro / Premium)

## Teknoloji Stack

| Katman | Teknoloji |
|--------|-----------|
| Frontend | Next.js 14 (App Router), TypeScript, Tailwind CSS, shadcn/ui |
| State | Zustand + React Query |
| Backend API | Next.js API Routes |
| AI Microservice | Python FastAPI |
| Veritabanı | PostgreSQL (Prisma ORM) |
| Cache | Redis |
| STT | ElevenLabs Scribe v2 Batch |
| AI Soru Üretme | OpenAI GPT-4 Turbo |
| Video Kayıt | MediaRecorder + Canvas Compositor (getDisplayMedia + getUserMedia) |
| Video Analizi | MediaPipe Face Mesh, face-api.js |
| Ses Analizi | librosa (Python) |
| Ödeme | Stripe / Iyzico |
| Storage | AWS S3 / Cloudflare R2 |
| Auth | NextAuth.js v5 + JWT |
| Hosting | Vercel (Frontend) + AWS EC2 / Render (Python) |

## Hızlı Başlangıç

```bash
# 1. Repo'yu klonla
git clone https://github.com/HakaniOzdogan/Yapay_Zeka_Mulakat.git
cd Yapay_Zeka_Mulakat

# 2. Bağımlılıkları yükle
npm install

# 3. Environment variables
cp .env.example .env.local
# .env.local dosyasını düzenle (bkz. docs/08-DEPLOYMENT.md)

# 4. Veritabanını başlat
docker-compose up -d

# 5. Prisma migration
npx prisma migrate dev
npx prisma generate
npx prisma db seed

# 6. Python servisini başlat (ayrı terminal)
cd python-service
python -m venv venv
source venv/bin/activate
pip install -r requirements.txt
uvicorn main:app --reload --port 8000

# 7. Next.js geliştirme sunucusu
cd ..
npm run dev
```

Tarayıcıda `http://localhost:3000` adresini aç.

## Dokümantasyon

Detaylı teknik dokümantasyon `docs/` klasöründe bulunur:

| Dosya | İçerik |
|-------|--------|
| [01-ARCHITECTURE.md](docs/01-ARCHITECTURE.md) | Sistem mimarisi, servis haritası, veri akışı |
| [02-DATABASE.md](docs/02-DATABASE.md) | Prisma şeması, model ilişkileri, indexler |
| [03-API_ENDPOINTS.md](docs/03-API_ENDPOINTS.md) | Tüm REST endpoint'leri, istek/cevap formatları |
| [04-AI_INTEGRATION.md](docs/04-AI_INTEGRATION.md) | STT (Scribe v2), soru üretme, cevap değerlendirme |
| [05-VIDEO_RECORDING.md](docs/05-VIDEO_RECORDING.md) | DualStreamRecorder, canvas PiP, upload stratejisi |
| [06-AUTHENTICATION.md](docs/06-AUTHENTICATION.md) | NextAuth konfigürasyonu, güvenlik önlemleri |
| [07-FRONTEND.md](docs/07-FRONTEND.md) | Sayfa yapısı, bileşenler, state management |
| [08-DEPLOYMENT.md](docs/08-DEPLOYMENT.md) | Docker, Vercel, AWS, environment variables |
| [09-TESTING.md](docs/09-TESTING.md) | Test stratejisi, unit/integration/e2e |
| [10-MONETIZATION.md](docs/10-MONETIZATION.md) | Paket yapısı, ödeme akışı, feature gate |
| [CHANGELOG.md](CHANGELOG.md) | Sürüm geçmişi ve tasarım değişiklikleri |

## Proje Yapısı (Özet)

```
Yapay_Zeka_Mulakat/
├── src/app/                    # Next.js 14 App Router sayfaları
│   ├── (auth)/                 # Login, register
│   ├── (dashboard)/            # Dashboard, mülakat listesi
│   └── api/                    # Backend API route'ları
├── src/components/             # React bileşenleri
├── src/lib/                    # Utility, API client, AI, video, audio
├── src/hooks/                  # Custom React hook'ları
├── src/store/                  # Zustand store'ları
├── src/types/                  # TypeScript tip tanımları
├── prisma/                     # Veritabanı şeması ve migration'lar
├── python-service/             # FastAPI AI microservice
├── public/                     # Statik dosyalar, ML modelleri
├── docs/                       # Teknik dokümantasyon
└── tests/                      # Test dosyaları
```

Detaylı dosya yapısı için bkz. [07-FRONTEND.md](docs/07-FRONTEND.md).

## Lisans

Bu proje özel kullanım içindir.
