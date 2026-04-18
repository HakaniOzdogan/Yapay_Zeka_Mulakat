#!/usr/bin/env bash
set -euo pipefail

COMPOSE_BASE="${COMPOSE_BASE:-docker/docker-compose.yml}"
COMPOSE_PROD="${COMPOSE_PROD:-docker/docker-compose.prod.yml}"
ENV_FILE="${ENV_FILE:-docker/.env.production}"
POSTGRES_SERVICE="${POSTGRES_SERVICE:-postgres}"
BACKUP_DIR="${BACKUP_DIR:-backups}"

mkdir -p "${BACKUP_DIR}"
TIMESTAMP="$(date -u +%Y%m%d_%H%M%S)"
OUT_FILE="${BACKUP_DIR}/postgres_${TIMESTAMP}.sql.gz"

echo "[backup] writing ${OUT_FILE}"

docker compose \
  --env-file "${ENV_FILE}" \
  -f "${COMPOSE_BASE}" \
  -f "${COMPOSE_PROD}" \
  exec -T "${POSTGRES_SERVICE}" \
  sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB"' \
  | gzip -9 > "${OUT_FILE}"

echo "[backup] done: ${OUT_FILE}"
