# Operations Runbook

## 1. Production Deployment (First Deploy)
1. Prepare host:
   - Install Docker Engine + Docker Compose plugin.
   - Open only ports `80` and `443` on public firewall.
   - Keep `5432`, `8080`, `8000`, `11434` private.
2. Create production env file:
   - `cp docker/.env.production.example docker/.env.production`
   - Fill required values:
     - `POSTGRES_PASSWORD`
     - `AUTH_JWT_KEY`
     - `CORS_ALLOWED_ORIGIN`
     - `FRONTEND_PUBLIC_BASE_URL`
     - `API_PUBLIC_BASE_URL`
3. Place TLS certificates:
   - `docker/nginx/certs/fullchain.pem`
   - `docker/nginx/certs/privkey.pem`
4. Validate compose configuration:
   - `docker compose -f docker/docker-compose.yml -f docker/docker-compose.prod.yml --env-file docker/.env.production config`
5. Start core stack:
   - `docker compose -f docker/docker-compose.yml -f docker/docker-compose.prod.yml --env-file docker/.env.production up -d --build`
6. Optional services:
   - Ollama: add `--profile ollama`
   - Speech-service: add `--profile speech`

## 2. Upgrade Procedure (Simple Downtime)
1. Backup database:
   - `bash scripts/ops/backup_postgres.sh`
2. Pull latest code.
3. Rebuild and restart:
   - `docker compose -f docker/docker-compose.yml -f docker/docker-compose.prod.yml --env-file docker/.env.production up -d --build`
4. Verify health checklist (section 5).

## 3. Backup and Restore

### Backup
- PostgreSQL:
  - `bash scripts/ops/backup_postgres.sh`
  - Output: `backups/postgres_<timestamp>.sql.gz`
- Optional Ollama model backup:
  - `bash scripts/ops/backup_ollama_models.sh`
  - Output: `backups/ollama_models_<timestamp>.tar.gz`

### Restore
1. Choose backup file.
2. Run:
   - `bash scripts/ops/restore_postgres.sh backups/postgres_<timestamp>.sql.gz`
3. Script asks for explicit `RESTORE` confirmation.
4. After restore, restart API:
   - `docker compose -f docker/docker-compose.yml -f docker/docker-compose.prod.yml --env-file docker/.env.production restart api`

### Recommended Schedule
- PostgreSQL: daily full backup + weekly off-host copy.
- Retention cleanup: keep enabled (`Retention__Enabled=true`) to limit DB growth.

## 4. Health Verification Checklist
1. Proxy reachable:
   - `curl -I https://<your-domain>/`
2. API readiness:
   - `curl -f https://<your-domain>/health/ready`
3. API endpoint smoke:
   - `curl -f https://<your-domain>/api/sessions/recent`
4. Speech websocket path (if enabled):
   - confirm `/speech/ws/transcribe` reaches speech-service.
5. Container health:
   - `docker compose -f docker/docker-compose.yml -f docker/docker-compose.prod.yml ps`

## 5. Incident Quick Checks

### API unhealthy
- Check API logs:
  - `docker compose -f docker/docker-compose.yml -f docker/docker-compose.prod.yml logs -f api`
- Common causes:
  - invalid `ConnectionStrings__Default`
  - invalid `AUTH_JWT_KEY`
  - migration/startup DB connectivity failures

### DB not ready
- Check postgres logs:
  - `docker compose -f docker/docker-compose.yml -f docker/docker-compose.prod.yml logs -f postgres`
- Verify disk free space and credentials.

### Nginx 502
- Check nginx logs:
  - `docker compose -f docker/docker-compose.yml -f docker/docker-compose.prod.yml logs -f nginx`
- Confirm `api` and `frontend` are healthy.
- Confirm service names match compose (`api`, `frontend`, `speech-service`).

### Speech websocket failing
- Ensure speech profile is enabled:
  - start stack with `--profile speech`.
- Check speech logs:
  - `docker compose -f docker/docker-compose.yml -f docker/docker-compose.prod.yml logs -f speech-service`
- Confirm frontend uses `/speech` base path.

### Ollama unavailable
- Ensure ollama profile is enabled:
  - start stack with `--profile ollama`.
- Check ollama logs:
  - `docker compose -f docker/docker-compose.yml -f docker/docker-compose.prod.yml logs -f ollama`
- Pull model inside container if missing.

## 6. Log Locations and Commands
- Nginx logs:
  - inside container: `/var/log/nginx/access.log`, `/var/log/nginx/error.log`
  - mounted volume: `nginx_logs`
- API/frontend/postgres logs:
  - `docker compose ... logs -f <service>`
- Container logs rotate with json-file:
  - max-size `10m`, max-file `5`

## 7. Security Basics
- Rotate `AUTH_JWT_KEY` periodically and after incident.
- Rotate DB password and update `docker/.env.production`.
- Restrict public ports to `80/443` only.
- Keep host firewall enabled.
- Run regular OS and container image updates.
- Use least-privilege host access and SSH key auth.

## 8. Capacity and Host Sizing Notes
- Baseline small deployment:
  - 4 vCPU, 8 GB RAM (without heavy Ollama usage).
- With Ollama models:
  - prefer 8+ vCPU, 16+ GB RAM (more for larger models/GPU workloads).
- Disk planning:
  - PostgreSQL data volume growth
  - Ollama model volume growth
  - Backup retention directory (`backups/`)

## 9. TLS Renewal Note
- Use certbot or platform-managed certs.
- Keep cert files synced to:
  - `docker/nginx/certs/fullchain.pem`
  - `docker/nginx/certs/privkey.pem`
- Reload nginx after renewal:
  - `docker compose -f docker/docker-compose.yml -f docker/docker-compose.prod.yml --env-file docker/.env.production exec nginx nginx -s reload`

## 10. Environment Variable Priority and Critical Set
- Priority order:
  - runtime environment variables
  - `docker/.env.production`
  - defaults in compose files
- Required before go-live:
  - `POSTGRES_PASSWORD`
  - `AUTH_JWT_KEY`
  - `CORS_ALLOWED_ORIGIN`
  - `FRONTEND_PUBLIC_BASE_URL`
  - `API_PUBLIC_BASE_URL`
- Optional and feature-based:
  - `LLM_*` (needed when Ollama profile is used)
  - `SPEECH_MODEL` (needed when speech profile is used)
  - `TELEMETRY_*`, `RETENTION_*`, `PRIVACY_*`
