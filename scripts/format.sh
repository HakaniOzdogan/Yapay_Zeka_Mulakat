#!/usr/bin/env bash
set -euo pipefail

if dotnet format -h >/dev/null 2>&1; then
  dotnet format src/backend/InterviewCoach.sln
else
  echo "dotnet format not available, skipping backend formatting"
fi

(
  cd src/frontend
  npm ci
  npm run lint --if-present
)