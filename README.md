# AI Mülakat Hazırlık Platformu

Gerçekçi iş mülakatı simülasyonu yapan, webcam + ekran kaydeden, ses transkripsiyonu yapan ve yapay zeka destekli geri bildirim sunan tam yığın bir platform.

---

## Özellikler

- **Canlı Mülakat Simülasyonu** — Rol bazlı sorular, 8'e kadar adaptif soru (ilk 3 cevaba göre üretilir)
- **Webcam + Ekran Kaydı** — Her soru için ayrı video dosyası, ses dahil ekran kaydı
- **Konuşma Transkripsiyonu** — faster-whisper large-v3-turbo ile yüksek doğruluklu STT
- **MediaPipe Yüz Analizi** — Göz teması, duruş, fidget, blendshape sinyalleri (arousal, stress, Duchenne gülüş)
- **AI Koçluk Raporu** — Ollama üzerinden yerel LLM ile puanlama ve geri bildirim
- **Rapor Sayfası** — Soru bazlı metrikler, davranışsal sinyaller, transkript görüntüleme

---

## Tech Stack

| Katman | Teknoloji |
|---|---|
| Frontend | React 18 + Vite + TypeScript |
| Backend | .NET 8 Web API + EF Core |
| Veritabanı | PostgreSQL 16 |
| STT | faster-whisper (`large-v3-turbo`) |
| LLM | Ollama (`qwen2.5:7b-instruct`) |
| Yüz Analizi | MediaPipe FaceLandmarker + PoseLandmarker |
| Container | Docker Compose |

---

## Hızlı Başlangıç

### Gereksinimler

- Docker ve Docker Compose
- NVIDIA GPU (önerilir) veya CPU modu

### 1. Env dosyasını oluştur

```bash
cp .env.example docker/.env
```

Gerekirse `docker/.env` içindeki değerleri düzenle.

### 2. Servisleri başlat

```bash
cd docker
docker compose up -d
```

İlk çalıştırmada faster-whisper modeli indirilir (~1.5 GB), birkaç dakika sürebilir.

### 3. Ollama modeli yükle

```bash
docker exec -it $(docker ps -qf "name=ollama") ollama pull qwen2.5:7b-instruct
```

### 4. Uygulamayı aç

| Servis | URL |
|---|---|
| Frontend | http://localhost:5173 |
| Backend API | http://localhost:8080 |
| Speech Service | http://localhost:8000 |
| Ollama | http://localhost:11434 |

---

## Servis Mimarisi

```
Tarayıcı
  ├── Frontend (React/Vite) :5173
  │     ├── → Backend API (.NET) :8080
  │     │         └── PostgreSQL :5432
  │     └── → Speech Service (Python/faster-whisper) :8000
  └── Ollama (LLM) :11434
              └── Backend API bağlanır
```

---

## Environment Variables

Tüm değişkenler `.env.example` dosyasında açıklamalı olarak listelenmiştir.

| Dosya | İçerik |
|---|---|
| `.env.example` | Tüm değişkenlerin özeti (buradan başla) |
| `docker/.env.example` | Docker Compose değişkenleri |
| `src/frontend/.env.example` | Frontend (VITE_*) değişkenleri |
| `services/speech-service/.env.example` | Speech servisi ayarları |

---

## Proje Yapısı

```
├── docker/                  # Docker Compose ve env dosyaları
├── docs/                    # Mimari, operasyon rehberi
├── services/
│   └── speech-service/      # Python faster-whisper STT servisi
├── src/
│   ├── backend/             # .NET 8 Web API
│   └── frontend/            # React + Vite uygulaması
└── .env.example             # Tüm env değişkenleri (başlangıç noktası)
```

---

## Geliştirici Kurulumu

[CONTRIBUTING.md](CONTRIBUTING.md) dosyasına bak.

---

## Dokümantasyon

- [Mimari](docs/architecture.md)
- [Operasyon Rehberi](docs/OPERATIONS_RUNBOOK.md)
