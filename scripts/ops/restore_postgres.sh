#!/usr/bin/env bash
set -euo pipefail

COMPOSE_BASE="${COMPOSE_BASE:-docker/docker-compose.yml}"
COMPOSE_PROD="${COMPOSE_PROD:-docker/docker-compose.prod.yml}"
ENV_FILE="${ENV_FILE:-docker/.env.production}"
POSTGRES_SERVICE="${POSTGRES_SERVICE:-postgres}"

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <backup.sql.gz>"
  exit 1
fi

DUMP_FILE="$1"
if [[ ! -f "${DUMP_FILE}" ]]; then
  echo "[restore] file not found: ${DUMP_FILE}"
  exit 1
fi

echo "[restore] target service: ${POSTGRES_SERVICE}"
echo "[restore] dump file: ${DUMP_FILE}"
echo "[restore] this will DROP and recreate public schema in target database."
read -r -p "Type 'RESTORE' to continue: " CONFIRM

if [[ "${CONFIRM}" != "RESTORE" ]]; then
  echo "[restore] aborted."
  exit 1
fi

echo "[restore] resetting schema..."
docker compose \
  --env-file "${ENV_FILE}" \
  -f "${COMPOSE_BASE}" \
  -f "${COMPOSE_PROD}" \
  exec -T "${POSTGRES_SERVICE}" \
  sh -c 'psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;"'

echo "[restore] applying dump..."
gunzip -c "${DUMP_FILE}" | docker compose \
  --env-file "${ENV_FILE}" \
  -f "${COMPOSE_BASE}" \
  -f "${COMPOSE_PROD}" \
  exec -T "${POSTGRES_SERVICE}" \
  sh -c 'psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"'

echo "[restore] completed successfully."
