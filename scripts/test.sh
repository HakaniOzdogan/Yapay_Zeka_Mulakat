#!/usr/bin/env bash
set -euo pipefail

dotnet test src/backend/InterviewCoach.sln
(
  cd src/frontend
  npm ci
  npm run build
)