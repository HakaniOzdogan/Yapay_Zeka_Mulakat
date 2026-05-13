# 03 — Kurulum ve Çalıştırma Rehberi

## Gereksinimler

### Zorunlu

| Araç | Minimum Versiyon | Açıklama |
|------|-----------------|----------|
| Node.js | 18+ | Frontend build ve geliştirme |
| .NET SDK | 8.0+ | Backend API |
| PostgreSQL | 14+ | Veritabanı |
| Git | 2.30+ | Kaynak kod yönetimi |

### Opsiyonel

| Araç | Açıklama |
|------|----------|
| Docker + Docker Compose | Konteynerize çalıştırma |
| Ollama | Yerel LLM fallback |
| Python 3.10+ | Speech service (Whisper ASR) |
| CUDA Toolkit | GPU hızlandırmalı Whisper |

## Adım Adım Kurulum

### 1. Repoyu Klonla

```bash
git clone https://github.com/HakaniOzdogan/Yapay_Zeka_Mulakat.git
cd Yapay_Zeka_Mulakat
```

### 2. PostgreSQL Veritabanı

```bash
# PostgreSQL kuruluysa:
psql -U postgres -c "CREATE USER coach WITH PASSWORD 'coachpass';"
psql -U postgres -c "CREATE DATABASE interviewcoach OWNER coach;"
```

Alternatif olarak Docker ile:
```bash
docker run -d \
  --name interview-pg \
  -e POSTGRES_USER=coach \
  -e POSTGRES_PASSWORD=coachpass \
  -e POSTGRES_DB=interviewcoach \
  -p 5432:5432 \
  postgres:16-alpine
```

### 3. Backend Kurulumu

```bash
cd src/backend/InterviewCoach.Api

# Bağımlılıkları yükle
dotnet restore

# Veritabanı migration'larını uygula
dotnet ef database update

# Çalıştır
dotnet run
```

Backend `http://localhost:8080` adresinde ayağa kalkar. Swagger UI: `http://localhost:8080/swagger`

### 4. Frontend Kurulumu

```bash
cd src/frontend

# Bağımlılıkları yükle
npm install

# Geliştirme sunucusunu başlat
npm run dev
```

Frontend `http://localhost:5173` adresinde açılır.

### 5. Ortam Değişkenleri

Frontend `.env` dosyası (`src/frontend/.env`):
```env
VITE_API_URL=http://localhost:8080/api
```

Backend `appsettings.Development.json` (önemli alanlar):
```json
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Database=interviewcoach;Username=coach;Password=coachpass"
  },
  "Llm": {
    "Provider": "Anthropic",
    "ApiKey": "sk-ant-api03-SENIN-KEY-IN",
    "BaseUrl": "https://api.anthropic.com",
    "PrimaryModel": "claude-sonnet-4-6",
    "FallbackProvider": "Ollama",
    "FallbackBaseUrl": "http://localhost:11434",
    "FallbackModel": "qwen2.5:7b-instruct"
  }
}
```

## Ollama Kurulumu (Fallback LLM)

```bash
# Ollama'yı kur
curl -fsSL https://ollama.com/install.sh | sh

# Modeli indir
ollama pull qwen2.5:7b-instruct

# Ollama servisi çalışıyor mu kontrol et
curl http://localhost:11434/api/tags
```

## Speech Service Kurulumu (Opsiyonel)

```bash
cd services/speech-service

# Python sanal ortam
python -m venv venv
source venv/bin/activate  # Windows: venv\Scripts\activate

# Bağımlılıklar
pip install -r requirements.txt

# Çalıştır
uvicorn app.main:app --host 0.0.0.0 --port 8765
```

## Docker ile Tam Kurulum

```bash
# Tüm servisleri başlat
docker compose up -d

# Logları takip et
docker compose logs -f
```

## Sık Karşılaşılan Sorunlar

### Veritabanı bağlantı hatası
Connection string'i kontrol edin. PostgreSQL servisinin çalıştığından emin olun:
```bash
pg_isready -h localhost -p 5432
```

### CORS hatası
Backend `appsettings.json` dosyasında `Cors.AllowedOrigin` değerinin frontend URL'si ile eşleştiğinden emin olun.

### MediaPipe yüklenmiyor
Tarayıcıda HTTPS gerekebilir. Localhost'ta HTTP ile çalışır ama deploy'da SSL sertifikası zorunludur.

### Ollama timeout
Büyük modellerde ilk çağrı yavaş olabilir. `TimeoutSeconds` değerini artırın veya daha küçük model kullanın.
