# 08 — Deployment ve Ortam Değişkenleri

## .env.example

Repo'da `.env.example` bulunur. `.env.local` asla commit'lenmez (`.gitignore`'da zorunlu).

```bash
# .env.example

# ─── VERİTABANI ───────────────────────────────────────────────
# PostgreSQL (local: docker-compose ile, production: Supabase/Neon/Railway)
DATABASE_URL="postgresql://USER:PASSWORD@HOST:5432/interview_platform?schema=public&sslmode=require"

# ─── NEXTAUTH ────────────────────────────────────────────────
# openssl rand -base64 32 komutuyla oluştur
NEXTAUTH_SECRET="BURAYA_RASTGELE_32_KARAKTER_GIR"
NEXTAUTH_URL="http://localhost:3000"

# ─── GOOGLE OAUTH ────────────────────────────────────────────
# console.cloud.google.com → Credentials → OAuth 2.0
GOOGLE_CLIENT_ID="xxx.apps.googleusercontent.com"
GOOGLE_CLIENT_SECRET="GOCSPX-xxx"

# ─── OPENAI ──────────────────────────────────────────────────
OPENAI_API_KEY="sk-proj-xxx"
OPENAI_MODEL="gpt-4-turbo"

# ─── ELEVENLABS (STT) ────────────────────────────────────────
ELEVENLABS_API_KEY="sk_xxx"

# ─── AWS S3 ──────────────────────────────────────────────────
AWS_REGION="eu-central-1"
AWS_ACCESS_KEY_ID="AKIAxxx"
AWS_SECRET_ACCESS_KEY="xxx"
AWS_S3_BUCKET="interview-platform-videos"
NEXT_PUBLIC_S3_BUCKET="interview-platform-videos"

# ─── REDIS (Upstash) ─────────────────────────────────────────
# upstash.com → Redis → Connect → .env
UPSTASH_REDIS_REST_URL="https://xxx.upstash.io"
UPSTASH_REDIS_REST_TOKEN="xxx"

# ─── ÖDEME ───────────────────────────────────────────────────
STRIPE_SECRET_KEY="sk_test_xxx"
STRIPE_WEBHOOK_SECRET="whsec_xxx"
NEXT_PUBLIC_STRIPE_PUBLISHABLE_KEY="pk_test_xxx"

# Iyzico (TR alternatif)
IYZICO_API_KEY="xxx"
IYZICO_SECRET_KEY="xxx"
IYZICO_BASE_URL="https://sandbox-api.iyzipay.com"

# ─── PYTHON SERVİSİ ──────────────────────────────────────────
PYTHON_SERVICE_URL="http://localhost:8000"
PYTHON_SERVICE_API_KEY="xxx"

# ─── ADMIN ───────────────────────────────────────────────────
ADMIN_EMAIL="admin@example.com"

# ─── GENEL ───────────────────────────────────────────────────
FRONTEND_URL="http://localhost:3000"
NODE_ENV="development"
```

## .gitignore Zorunlu Kontroller

```gitignore
# .gitignore — Bu satırların MUTLAKA var olduğunu doğrula

# Environment
.env
.env.local
.env.*.local
.env.development
.env.production

# Build
.next/
out/
dist/

# Node
node_modules/

# Python
python-service/venv/
python-service/__pycache__/
python-service/.env

# OS
.DS_Store
Thumbs.db

# IDE
.vscode/settings.json
.idea/
```

## Docker Compose (Yerel Geliştirme)

```yaml
# docker-compose.yml

services:
  postgres:
    image: postgres:16-alpine
    environment:
      POSTGRES_USER: interview
      POSTGRES_PASSWORD: interview123
      POSTGRES_DB: interview_platform
    ports:
      - "5432:5432"
    volumes:
      - postgres_data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U interview"]
      interval: 5s
      timeout: 5s
      retries: 5

  redis:
    image: redis:7-alpine
    ports:
      - "6379:6379"
    command: redis-server --maxmemory 256mb --maxmemory-policy allkeys-lru

volumes:
  postgres_data:
```

Başlatma:
```bash
docker-compose up -d
```

## Kurulum Adımları (Sıfırdan)

```bash
# 1. Repo
git clone https://github.com/HakaniOzdogan/Yapay_Zeka_Mulakat.git
cd Yapay_Zeka_Mulakat

# 2. Node bağımlılıkları
npm install

# 3. Ortam değişkenleri
cp .env.example .env.local
# .env.local dosyasını düzenle

# 4. Veritabanı kur
docker-compose up -d
sleep 5  # postgres'in ayağa kalkmasını bekle

# 5. Prisma migration
npx prisma migrate dev --name init
npx prisma generate
npx prisma db seed

# 6. Python servisi
cd python-service
python -m venv venv
source venv/bin/activate          # Windows: venv\Scripts\activate
pip install -r requirements.txt
uvicorn main:app --reload --port 8000
cd ..

# 7. Next.js
npm run dev
```

## Vercel Deploy (Production)

### 1. Ortam Değişkenleri

Vercel Dashboard → Project → Settings → Environment Variables:

Aşağıdaki değişkenlerin hepsini ekle (`.env.example` listesi). `NEXTAUTH_URL`'i production URL ile güncelle:
```
NEXTAUTH_URL=https://senin-domain.vercel.app
```

### 2. Build Ayarları

```json
// vercel.json
{
  "buildCommand": "npx prisma generate && npm run build",
  "functions": {
    "src/app/api/**": {
      "maxDuration": 60
    }
  }
}
```

### 3. Database Migration (Production)

```bash
# CI/CD sırasında veya manuel olarak:
DATABASE_URL="production-url" npx prisma migrate deploy
```

### 4. Stripe Webhook

Vercel deploy sonrası Stripe Dashboard → Webhooks → Add endpoint:
- URL: `https://senin-domain.vercel.app/api/payment/webhook`
- Events: `checkout.session.completed`, `customer.subscription.deleted`
- `STRIPE_WEBHOOK_SECRET`'i webhook secret ile güncelle

## Python Servisi Deploy (Render)

```yaml
# python-service/render.yaml

services:
  - type: web
    name: interview-ai-service
    env: python
    buildCommand: pip install -r requirements.txt
    startCommand: uvicorn main:app --host 0.0.0.0 --port $PORT
    envVars:
      - key: ELEVENLABS_API_KEY
        sync: false
      - key: OPENAI_API_KEY
        sync: false
```

Deploy sonrası `PYTHON_SERVICE_URL`'i Render URL ile güncelle.

## AWS S3 Bucket Ayarları

```json
// S3 Bucket Policy (public erişimi kapat, sadece presigned URL)
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "DenyPublicAccess",
      "Effect": "Deny",
      "Principal": "*",
      "Action": "s3:GetObject",
      "Resource": "arn:aws:s3:::interview-platform-videos/*",
      "Condition": {
        "StringNotLike": {
          "aws:userid": "ARN_OF_YOUR_IAM_USER"
        }
      }
    }
  ]
}
```

CORS:
```json
[
  {
    "AllowedHeaders": ["*"],
    "AllowedMethods": ["GET", "PUT", "POST", "DELETE"],
    "AllowedOrigins": ["https://senin-domain.vercel.app"],
    "ExposeHeaders": ["ETag"],
    "MaxAgeSeconds": 3000
  }
]
```

## GitHub Actions CI

```yaml
# .github/workflows/ci.yml

name: CI

on:
  push:
    branches: [main, develop]
  pull_request:
    branches: [main]

jobs:
  check:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-node@v4
        with:
          node-version: '20'
          cache: 'npm'

      - run: npm ci

      - name: TypeScript tip kontrolü
        run: npx tsc --noEmit

      - name: ESLint
        run: npm run lint

      - name: Prisma validate
        run: npx prisma validate

      - name: Testler
        run: npm run test
        env:
          DATABASE_URL: ${{ secrets.TEST_DATABASE_URL }}
```

## tsconfig.json — Strict Mode

TypeScript strict mode mutlaka açık olmalı:

```json
{
  "compilerOptions": {
    "strict": true,
    "noImplicitAny": true,
    "strictNullChecks": true,
    "noUnusedLocals": true,
    "noUnusedParameters": true,
    "target": "ES2017",
    "lib": ["dom", "dom.iterable", "esnext"],
    "module": "esnext",
    "moduleResolution": "bundler",
    "jsx": "preserve",
    "incremental": true,
    "plugins": [{ "name": "next" }],
    "paths": {
      "@/*": ["./src/*"]
    }
  },
  "include": ["next-env.d.ts", "**/*.ts", "**/*.tsx", ".next/types/**/*.ts"],
  "exclude": ["node_modules"]
}
```
