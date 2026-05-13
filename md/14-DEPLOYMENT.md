# 14 — Yayına Alma ve Docker Konfigürasyonu

## Deployment Seçenekleri

### 1. Yerel Geliştirme (Development)

En basit kurulum. Tüm servisleri doğrudan çalıştırır.

```bash
# Terminal 1: PostgreSQL (Docker ile)
docker run -d --name pg -e POSTGRES_USER=coach -e POSTGRES_PASSWORD=coachpass -e POSTGRES_DB=interviewcoach -p 5432:5432 postgres:16-alpine

# Terminal 2: Backend
cd src/backend/InterviewCoach.Api
dotnet run

# Terminal 3: Frontend
cd src/frontend
npm run dev

# Terminal 4: Ollama (opsiyonel)
ollama serve
```

### 2. Docker Compose (Önerilen)

Tüm servisleri tek komutla başlatır.

```yaml
# docker-compose.yml
version: '3.8'

services:
  postgres:
    image: postgres:16-alpine
    environment:
      POSTGRES_USER: coach
      POSTGRES_PASSWORD: coachpass
      POSTGRES_DB: interviewcoach
    ports:
      - "5432:5432"
    volumes:
      - pgdata:/var/lib/postgresql/data

  backend:
    build: src/backend
    ports:
      - "8080:8080"
    environment:
      ConnectionStrings__Default: "Host=postgres;Port=5432;Database=interviewcoach;Username=coach;Password=coachpass"
      Llm__Provider: Anthropic
      Llm__ApiKey: ${CLAUDE_API_KEY}
      Llm__BaseUrl: https://api.anthropic.com
      Llm__PrimaryModel: claude-sonnet-4-6
      Llm__Fallback__Provider: Ollama
      Llm__Fallback__BaseUrl: http://ollama:11434
    depends_on:
      - postgres

  frontend:
    build: src/frontend
    ports:
      - "80:80"
    environment:
      VITE_API_URL: http://localhost:8080/api

  speech-service:
    build: services/speech-service
    ports:
      - "8765:8765"

  ollama:
    image: ollama/ollama
    ports:
      - "11434:11434"
    volumes:
      - ollama-models:/root/.ollama

volumes:
  pgdata:
  ollama-models:
```

Başlatma:
```bash
# .env dosyasına API key'i ekle
echo "CLAUDE_API_KEY=sk-ant-api03-..." > .env

# Tüm servisleri başlat
docker compose up -d

# Ollama'da model indir (ilk seferde)
docker compose exec ollama ollama pull qwen2.5:7b-instruct
```

### 3. VPS / Cloud Deployment

Üretim ortamı için ek adımlar:

**SSL Sertifikası:** Let's Encrypt ile ücretsiz SSL. Nginx reverse proxy arkasında çalıştır.

**Nginx Konfigürasyonu:**
```nginx
server {
    listen 443 ssl;
    server_name interviewai.example.com;

    ssl_certificate /etc/letsencrypt/live/interviewai.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/interviewai.example.com/privkey.pem;

    # Frontend
    location / {
        proxy_pass http://localhost:80;
    }

    # Backend API
    location /api/ {
        proxy_pass http://localhost:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
    }

    # WebSocket (Speech Service)
    location /ws/ {
        proxy_pass http://localhost:8765;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
    }
}
```

**Ortam Değişkenleri (Production):**
- `Llm__ApiKey` — Anthropic API key (gizli tutulmalı)
- `Auth__JwtKey` — Güçlü rastgele anahtar (min 32 karakter)
- `Auth__SeedAdminEmail` / `Auth__SeedAdminPassword` — İlk admin
- `Cors__AllowedOrigin` — Frontend domain
- `ConnectionStrings__Default` — Production DB bağlantısı

## Frontend Build

```bash
cd src/frontend

# Production build
npm run build

# Build çıktısı dist/ klasöründe
# Nginx veya herhangi bir static file server ile sunulabilir
```

## Backend Build

```bash
cd src/backend/InterviewCoach.Api

# Release build
dotnet publish -c Release -o ./publish

# Çalıştır
cd publish
dotnet InterviewCoach.Api.dll
```

## Sağlık Kontrolü

```bash
# Backend çalışıyor mu?
curl http://localhost:8080/api/health

# PostgreSQL bağlantısı?
curl http://localhost:8080/api/health/db

# Ollama erişilebilir mi?
curl http://localhost:11434/api/tags
```

## Yedekleme

```bash
# PostgreSQL veritabanı yedeği
pg_dump -h localhost -U coach interviewcoach > backup_$(date +%Y%m%d).sql

# Geri yükleme
psql -h localhost -U coach interviewcoach < backup_20260513.sql
```

## Monitoring

Konfigürasyonda OpenTelemetry desteği bulunur:

```json
{
  "Telemetry": {
    "Enabled": true,
    "OtlpEndpoint": "http://otel-collector:4317",
    "ServiceName": "InterviewCoach",
    "ServiceVersion": "1.0.0"
  }
}
```

Jaeger, Prometheus veya Grafana ile entegre edilebilir.
