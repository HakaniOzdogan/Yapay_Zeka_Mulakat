# Operations Runbook

## 1. İlk Üretim Dağıtımı

### 1.1 Sunucu Hazırlığı
- Docker Engine ve Docker Compose plugin kurun.
- Güvenlik duvarında yalnızca `80` ve `443` portlarını dışarıya açın.
- `5432`, `8080`, `8000`, `11434` portlarını kapalı tutun (sunucu içi iletişim için yeterli).

### 1.2 Ortam Dosyası Oluşturma
```bash
cp docker/.env.production.example docker/.env.production
```

Aşağıdaki zorunlu değerleri doldurun:

| Değişken | Açıklama |
|----------|---------|
| `POSTGRES_PASSWORD` | Güçlü, rastgele bir veritabanı şifresi |
| `AUTH_JWT_KEY` | En az 32 karakter rastgele string |
| `CORS_ALLOWED_ORIGIN` | `https://alan-adiniz.com` |
| `FRONTEND_PUBLIC_BASE_URL` | `https://alan-adiniz.com` |
| `API_PUBLIC_BASE_URL` | `https://alan-adiniz.com/api` |
| `APP_DOMAIN` | `alan-adiniz.com` |

### 1.3 TLS Sertifikaları
Sertifika dosyalarını şu konumlara yerleştirin:
```
docker/nginx/certs/fullchain.pem
docker/nginx/certs/privkey.pem
```

Let's Encrypt kullanıyorsanız:
```bash
certbot certonly --standalone -d alan-adiniz.com
cp /etc/letsencrypt/live/alan-adiniz.com/fullchain.pem docker/nginx/certs/
cp /etc/letsencrypt/live/alan-adiniz.com/privkey.pem   docker/nginx/certs/
```

### 1.4 Compose Yapılandırmasını Doğrula
```bash
docker compose \
  -f docker/docker-compose.yml \
  -f docker/docker-compose.prod.yml \
  --env-file docker/.env.production \
  config
```

### 1.5 Çekirdek Stack'i Başlat
```bash
docker compose \
  -f docker/docker-compose.yml \
  -f docker/docker-compose.prod.yml \
  --env-file docker/.env.production \
  up -d --build
```

### 1.6 Veritabanı Şemasını Oluştur (İlk Kez)
API sağlıklı duruma geçtikten sonra migration'ı çalıştırın:
```bash
docker compose \
  -f docker/docker-compose.yml \
  -f docker/docker-compose.prod.yml \
  --env-file docker/.env.production \
  exec api dotnet ef database update
```

> API'nin `service_healthy` durumuna geçmesini bekleyin. `docker compose ps` ile kontrol edebilirsiniz.

### 1.7 Opsiyonel Servisler

**Ollama (yerel LLM):**
```bash
docker compose \
  -f docker/docker-compose.yml \
  -f docker/docker-compose.prod.yml \
  --env-file docker/.env.production \
  --profile ollama up -d
```

Ardından modeli indirin:
```bash
docker compose \
  -f docker/docker-compose.yml \
  -f docker/docker-compose.prod.yml \
  exec ollama ollama pull qwen2.5:7b-instruct
```

**Konuşma Servisi (Faster-Whisper ASR):**
```bash
docker compose \
  -f docker/docker-compose.yml \
  -f docker/docker-compose.prod.yml \
  --env-file docker/.env.production \
  --profile speech up -d
```

---

## 2. Güncelleme (Kısa Kesinti ile)

1. Veritabanını yedekle:
   ```bash
   bash scripts/ops/backup_postgres.sh
   ```
2. Son kodu çek: `git pull`
3. Yeniden derle ve başlat:
   ```bash
   docker compose \
     -f docker/docker-compose.yml \
     -f docker/docker-compose.prod.yml \
     --env-file docker/.env.production \
     up -d --build
   ```
4. Gerekiyorsa migration çalıştır (Bölüm 1.6).
5. Sağlık kontrolünü doğrula (Bölüm 4).

---

## 3. Yedekleme ve Geri Yükleme

### Yedekleme

```bash
# PostgreSQL
bash scripts/ops/backup_postgres.sh
# Çıktı: backups/postgres_<zaman_damgası>.sql.gz

# Ollama modelleri (opsiyonel)
bash scripts/ops/backup_ollama_models.sh
# Çıktı: backups/ollama_models_<zaman_damgası>.tar.gz
```

**Önerilen takvim:** Günlük tam PostgreSQL yedeği + haftalık sunucu dışı kopya.

### Geri Yükleme

```bash
bash scripts/ops/restore_postgres.sh backups/postgres_<zaman_damgası>.sql.gz
```

Script açıkça `RESTORE` onayı ister. Geri yüklemeden sonra API'yi yeniden başlatın:
```bash
docker compose \
  -f docker/docker-compose.yml \
  -f docker/docker-compose.prod.yml \
  --env-file docker/.env.production \
  restart api
```

---

## 4. Sağlık Doğrulama Kontrol Listesi

```bash
# 1. Proxy erişilebilir mi?
curl -I https://alan-adiniz.com/

# 2. API hazır mı?
curl -f https://alan-adiniz.com/health/ready

# 3. API endpoint smoke test
curl -f -H "Authorization: Bearer <token>" \
  https://alan-adiniz.com/api/sessions/recent

# 4. Container sağlık durumları
docker compose \
  -f docker/docker-compose.yml \
  -f docker/docker-compose.prod.yml \
  ps
```

---

## 5. Olay Hızlı Kontrolleri

### API sağlıksız
```bash
docker compose -f docker/docker-compose.yml -f docker/docker-compose.prod.yml logs -f api
```
Yaygın nedenler:
- Geçersiz `ConnectionStrings__Default` (postgres henüz hazır değil)
- Geçersiz `AUTH_JWT_KEY` (çok kısa veya eksik)
- Migration çalıştırılmamış (tablolar yok)

### Veritabanı hazır değil
```bash
docker compose -f docker/docker-compose.yml -f docker/docker-compose.prod.yml logs -f postgres
```
Disk alanını ve şifreyi kontrol edin.

### Nginx 502
```bash
docker compose -f docker/docker-compose.yml -f docker/docker-compose.prod.yml logs -f nginx
```
- `api` ve `frontend` container'larının sağlıklı olduğunu doğrulayın.
- Servis adlarının compose dosyasındakilerle (`api`, `frontend`) eşleştiğini kontrol edin.

### Ollama kullanılamıyor
```bash
docker compose -f docker/docker-compose.yml -f docker/docker-compose.prod.yml logs -f ollama
```
- `--profile ollama` ile başlatıldığından emin olun.
- Model eksikse içeride indirin: `docker exec <ollama-container> ollama pull qwen2.5:7b-instruct`

### Konuşma servisi yanıt vermiyor
```bash
docker compose -f docker/docker-compose.yml -f docker/docker-compose.prod.yml logs -f speech-service
```
- `--profile speech` ile başlatıldığından emin olun.
- İlk başlangıçta model indirmesi ~90 saniye sürebilir (healthcheck `start_period: 90s`).

---

## 6. Log Konumları

| Servis | Konum |
|--------|-------|
| Nginx erişim/hata | Container içi: `/var/log/nginx/`, Volume: `nginx_logs` |
| API, frontend, postgres | `docker compose ... logs -f <servis>` |

Log rotasyonu: `json-file` sürücüsü, maks. 10 MB / 5 dosya.

---

## 7. Güvenlik Temelleri

- `AUTH_JWT_KEY`'i periyodik olarak ve her olaydan sonra değiştirin.
- DB şifresini değiştirdikten sonra `docker/.env.production` dosyasını güncelleyin.
- Genel portları yalnızca `80/443` ile sınırlı tutun.
- Düzenli işletim sistemi ve container image güncellemeleri yapın.
- Sunucuya yalnızca SSH key ile erişin.

---

## 8. Kapasite ve Sunucu Boyutlandırma

| Senaryo | Minimum Öneri |
|---------|--------------|
| Temel (Ollama olmadan) | 4 vCPU, 8 GB RAM |
| Ollama ile (7B model) | 8+ vCPU, 16+ GB RAM |
| GPU hızlandırma | NVIDIA GPU (CUDA 12+), 8+ GB VRAM |

**Disk planlaması:**
- PostgreSQL volume: Kullanım başına büyür (retention 30 gün varsayılan)
- Ollama model volume: ~5 GB (7B int4), ~15 GB (13B)
- Yedekleme dizini: Günlük backup boyutuna göre

---

## 9. TLS Yenileme

**Certbot ile otomatik yenileme:**
```bash
certbot renew --quiet
cp /etc/letsencrypt/live/alan-adiniz.com/fullchain.pem docker/nginx/certs/
cp /etc/letsencrypt/live/alan-adiniz.com/privkey.pem   docker/nginx/certs/
docker compose \
  -f docker/docker-compose.yml \
  -f docker/docker-compose.prod.yml \
  --env-file docker/.env.production \
  exec nginx nginx -s reload
```

Cron ile otomatikleştirmek için (`/etc/cron.d/certbot-renew`):
```
0 3 * * * root certbot renew --quiet && \
  cp /etc/letsencrypt/live/<domain>/fullchain.pem /path/to/docker/nginx/certs/ && \
  cp /etc/letsencrypt/live/<domain>/privkey.pem   /path/to/docker/nginx/certs/ && \
  docker compose -f /path/to/docker/docker-compose.yml -f /path/to/docker/docker-compose.prod.yml exec nginx nginx -s reload
```

---

## 10. Ortam Değişkeni Referansı

**Öncelik sırası:** çalışma zamanı ortam değişkenleri > `docker/.env.production` > compose dosyası varsayılanları

### Zorunlu (go-live öncesi doldurulmalı)

| Değişken | Açıklama |
|----------|---------|
| `POSTGRES_PASSWORD` | Veritabanı şifresi |
| `AUTH_JWT_KEY` | JWT imzalama anahtarı (min. 32 karakter) |
| `CORS_ALLOWED_ORIGIN` | Frontend'in genel URL'si |
| `FRONTEND_PUBLIC_BASE_URL` | Frontend'in genel URL'si |
| `API_PUBLIC_BASE_URL` | API'nin genel URL'si |
| `APP_DOMAIN` | Alan adı (nginx için) |

### Opsiyonel

| Değişken | Varsayılan | Açıklama |
|----------|-----------|---------|
| `LLM_BASE_URL` | `http://ollama:11434` | Ollama endpoint'i |
| `LLM_MODEL` | `qwen2.5:7b-instruct` | Kullanılacak model |
| `LLM_TIMEOUT_SECONDS` | `60` | LLM yanıt zaman aşımı |
| `SPEECH_MODEL` | `small` | Whisper model boyutu |
| `TELEMETRY_ENABLED` | `false` | OpenTelemetry açma/kapama |
| `RETENTION_ENABLED` | `true` | Otomatik veri silme |
| `RETENTION_DELETE_AFTER_DAYS` | `30` | Kaç günden eski veriler silinir |
| `PRIVACY_REDACT_TRANSCRIPTS` | `true` | Transcript PII maskeleme |

---

## 11. Windows Geliştirici Ortamı

Üretim Docker Compose yerine, Windows ortamında geliştirme için kök dizindeki PowerShell scriptleri kullanılır.

### Başlatma
```powershell
# CPU modunda
.\start.ps1

# GPU ile (NVIDIA)
.\start.ps1 -GpuEnabled

# Farklı model ile
.\start.ps1 -GpuEnabled -SpeechModel medium
```

### Durdurma
```powershell
.\stop.ps1
```

Servisler `http://localhost:5173` (frontend) ve `http://localhost:8080` (API) adreslerinde başlar.
