# Geliştirici Rehberi

## Ön Gereksinimler

| Araç | Minimum Sürüm |
|---|---|
| Docker + Docker Compose | 24+ |
| Node.js | 20+ |
| .NET SDK | 8.0 |
| NVIDIA GPU + CUDA | Önerilir (CPU modu da çalışır) |

---

## Docker ile Hızlı Başlangıç

```bash
cp .env.example docker/.env
cd docker
docker compose up -d
```

Servisler hazır olduğunda:
- Frontend → http://localhost:5173
- Backend API → http://localhost:8080
- Speech Service → http://localhost:8000

Ollama modelini ilk kez yükle:
```bash
docker exec -it $(docker ps -qf "name=ollama") ollama pull qwen2.5:7b-instruct
```

---

## Servis Portları

| Port | Servis | Açıklama |
|---|---|---|
| 5173 | Frontend | React + Vite dev server |
| 8080 | Backend API | .NET 8 Web API |
| 8000 | Speech Service | Python faster-whisper STT |
| 11434 | Ollama | Yerel LLM |
| 5432 | PostgreSQL | Veritabanı |

---

## Geliştirme Ortamı (Docker Olmadan)

### Frontend

```bash
cd src/frontend
cp .env.example .env
npm install
npm run dev
```

### Backend

```bash
cd src/backend
dotnet restore
dotnet run --project InterviewCoach.Api
```

Veritabanı için PostgreSQL ve migration gerekir:
```bash
dotnet ef database update --project InterviewCoach.Infrastructure --startup-project InterviewCoach.Api
```

### Speech Service

```bash
cd services/speech-service
pip install -r requirements.txt
uvicorn app.main:app --reload --port 8000
```

---

## Commit Convention

[Conventional Commits](https://www.conventionalcommits.org/) standardını kullanıyoruz:

```
<tip>(<kapsam>): <açıklama>

feat(interview): add adaptive question generation
fix(recording): fix Q2+ audio chunk mismatch
docs(readme): update tech stack
refactor(transport): simplify session transport retry logic
```

| Tip | Ne Zaman |
|---|---|
| `feat` | Yeni özellik |
| `fix` | Hata düzeltme |
| `docs` | Dokümantasyon |
| `refactor` | Davranış değişmeden kod iyileştirme |
| `chore` | Bağımlılık güncellemesi, build |

---

## Proje Mimarisi

```
src/
├── backend/
│   ├── InterviewCoach.Api/          # Controller, Middleware, Program.cs
│   ├── InterviewCoach.Application/  # Use case servisleri
│   ├── InterviewCoach.Domain/       # Entity modeller
│   └── InterviewCoach.Infrastructure/ # EF Core, DB context
└── frontend/
    └── src/
        ├── pages/          # InterviewSession, Report, ReportsList, Home
        ├── services/       # ApiService, SessionTransport, RecordingBuffer
        ├── vision/         # MediaPipe feature extractors
        └── components/     # VideoCanvas, TranscriptModal

services/
└── speech-service/         # Python FastAPI + faster-whisper
    └── app/
        ├── main.py         # /transcribe endpoint
        └── backends/       # FasterWhisperBackend
```

---

## Sıkça Sorulan Durumlar

**Speech servisi model indirmiyor?**
```bash
docker logs $(docker ps -qf "name=speech") --tail 50
```

**Ollama modeli yok?**
```bash
docker exec -it $(docker ps -qf "name=ollama") ollama list
docker exec -it $(docker ps -qf "name=ollama") ollama pull qwen2.5:7b-instruct
```

**Ses kaydı tarayıcıda çalışmıyor?**
Mikrofon/kamera iznini site ayarlarından ver. HTTPS gerektiren tarayıcılarda localhost muafiyeti genellikle aktif.

**Migration hatası?**
```bash
cd src/backend
dotnet ef migrations add <MigrationName> --project InterviewCoach.Infrastructure --startup-project InterviewCoach.Api
dotnet ef database update --project InterviewCoach.Infrastructure --startup-project InterviewCoach.Api
```
