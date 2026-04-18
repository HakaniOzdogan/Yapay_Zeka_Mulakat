#!/usr/bin/env bash
set -euo pipefail

OLLAMA_VOLUME="${OLLAMA_VOLUME:-ollama_data_prod}"
BACKUP_DIR="${BACKUP_DIR:-backups}"

mkdir -p "${BACKUP_DIR}"
TIMESTAMP="$(date -u +%Y%m%d_%H%M%S)"
OUT_FILE="${BACKUP_DIR}/ollama_models_${TIMESTAMP}.tar.gz"

echo "[backup] creating ollama models backup from volume '${OLLAMA_VOLUME}'"

docker run --rm \
  -v "${OLLAMA_VOLUME}:/data:ro" \
  -v "$(pwd)/${BACKUP_DIR}:/backup" \
  alpine:3.20 \
  sh -c "tar -czf /backup/$(basename "${OUT_FILE}") -C /data ."

echo "[backup] done: ${OUT_FILE}"
