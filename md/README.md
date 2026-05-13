# Interview AI — Yapay Zeka Destekli Mülakat Sistemi

## Proje Hakkında

Interview AI, kullanıcıların teknik ve davranışsal mülakatlara hazırlanmasını sağlayan, yapay zeka destekli interaktif bir mülakat simülasyon platformudur. Sistem; gerçek zamanlı video kaydı, ses transkripti, görüntü tabanlı davranış analizi ve LLM destekli yetkinlik değerlendirmesi sunarak adayların mülakat performansını ölçülebilir hale getirir.

## Temel Özellikler

- **Gerçek Zamanlı Video Mülakat:** Kamera ve mikrofon ile canlı mülakat simülasyonu
- **Otomatik Transkript:** Whisper ASR ile konuşmanın yazıya dökülmesi
- **Görüntü Analizi:** MediaPipe ile göz teması, duruş, kafa hareketleri ve beden dili analizi
- **Yetkinlik Değerlendirmesi:** Claude API ile teknik ve davranışsal yetkinlik puanlaması
- **Kapsamlı Raporlama:** Soru bazlı video kayıtları, transkript, metrik analizi ve AI coaching
- **Çoklu LLM Desteği:** Claude (primary) + Ollama (fallback) mimarisi
- **Çoklu Dil:** Türkçe ve İngilizce mülakat desteği

## Teknik Stack

| Katman | Teknoloji |
|--------|-----------|
| Frontend | React 18 + TypeScript + Vite |
| Backend | ASP.NET Core 8 Web API |
| Veritabanı | PostgreSQL + EF Core |
| LLM (Primary) | Anthropic Claude API |
| LLM (Fallback) | Ollama (qwen2.5:7b-instruct) |
| Ses İşleme | Whisper ASR (FastAPI) |
| Görüntü Analizi | MediaPipe (Face + Pose) |
| Kimlik Doğrulama | JWT Bearer Token |

## Proje Yapısı

```
Yapay_Zeka_Mulakat/
├── src/
│   ├── frontend/               # React + Vite frontend
│   │   ├── src/
│   │   │   ├── pages/          # Sayfa bileşenleri
│   │   │   ├── components/     # Ortak bileşenler
│   │   │   ├── services/       # API, MediaPipe, Ses servisleri
│   │   │   ├── speech/         # Streaming ASR bağlantısı
│   │   │   ├── vision/         # Görüntü analiz modülleri
│   │   │   └── styles/         # CSS dosyaları
│   │   └── public/             # Statik dosyalar
│   └── backend/
│       ├── InterviewCoach.Api/          # Ana API projesi
│       ├── InterviewCoach.Domain/       # Domain modelleri
│       ├── InterviewCoach.Infrastructure/ # EF Core, Migrations
│       ├── InterviewCoach.Application/  # İş mantığı
│       └── InterviewCoach.Tests/        # Test projesi
├── services/
│   └── speech-service/         # Whisper ASR FastAPI servisi
├── docs/                       # Proje dokümantasyonu
└── prisma/                     # Prisma şeması (legacy)
```

## Hızlı Başlangıç

Detaylı kurulum için bkz. [docs/03-KURULUM.md](docs/03-KURULUM.md)

```bash
# 1. Repoyu klonla
git clone https://github.com/HakaniOzdogan/Yapay_Zeka_Mulakat.git
cd Yapay_Zeka_Mulakat

# 2. Backend
cd src/backend/InterviewCoach.Api
dotnet restore
dotnet run

# 3. Frontend
cd src/frontend
npm install
npm run dev

# 4. Tarayıcıda aç
open http://localhost:5173
```

## Dokümantasyon

| Dosya | İçerik |
|-------|--------|
| [01-PROJE-OZETI.md](docs/01-PROJE-OZETI.md) | Projenin amacı, hedefleri ve kapsamı |
| [02-MIMARI.md](docs/02-MIMARI.md) | Sistem mimarisi ve bileşen diyagramı |
| [03-KURULUM.md](docs/03-KURULUM.md) | Kurulum ve çalıştırma rehberi |
| [04-LLM-ENTEGRASYONU.md](docs/04-LLM-ENTEGRASYONU.md) | Claude + Ollama entegrasyonu |
| [05-MULAKAT-OTURUMU.md](docs/05-MULAKAT-OTURUMU.md) | Mülakat oturum akışı |
| [06-RAPOR-SISTEMI.md](docs/06-RAPOR-SISTEMI.md) | Rapor ve yetkinlik değerlendirmesi |
| [07-TRANSCRIPT-SISTEMI.md](docs/07-TRANSCRIPT-SISTEMI.md) | Transkript işleme sistemi |
| [08-GORUNTU-ANALIZI.md](docs/08-GORUNTU-ANALIZI.md) | MediaPipe görüntü analizi |
| [09-PUANLAMA-SISTEMI.md](docs/09-PUANLAMA-SISTEMI.md) | Puanlama profilleri ve hesaplama |
| [10-VERITABANI.md](docs/10-VERITABANI.md) | Veritabanı şeması ve veri modeli |
| [11-FRONTEND.md](docs/11-FRONTEND.md) | Frontend mimari rehberi |
| [12-BACKEND.md](docs/12-BACKEND.md) | Backend API rehberi |
| [13-API-REFERANS.md](docs/13-API-REFERANS.md) | REST API endpoint referansı |
| [14-DEPLOYMENT.md](docs/14-DEPLOYMENT.md) | Yayına alma ve Docker konfigürasyonu |
| [15-YOL-HARITASI.md](docs/15-YOL-HARITASI.md) | Gelecek planı ve geliştirme yol haritası |

## Lisans

Bu proje Isparta Uygulamalı Bilimler Üniversitesi Bilgisayar Mühendisliği Bölümü bünyesinde akademik amaçlı geliştirilmektedir.

## İletişim

- **Geliştirici:** Hakan İslam Özdoğan (2312729016)
- **Danışman:** Doç. Dr. Serap Ergün
- **Kurum:** Isparta Uygulamalı Bilimler Üniversitesi, Teknoloji Fakültesi
