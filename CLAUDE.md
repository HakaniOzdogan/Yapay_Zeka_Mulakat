# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

AI-powered interview coaching platform with webcam/screen recording, real-time facial analysis via MediaPipe, speech transcription via faster-whisper, and local LLM feedback via Ollama. All AI runs locally — no cloud API calls for inference.

## Architecture

5-service stack:

```
Browser
├── Frontend (React/Vite/TS) :5173
│   ├── Backend API (.NET 8)  :8080  ─── PostgreSQL 16 :5432
│   └── Speech Service (Python/faster-whisper) :8000
└── Ollama (local LLM) :11434
```

**Backend layers:**
- `InterviewCoach.Api` — Controllers, middleware, Program.cs (DI root)
- `InterviewCoach.Application` — Service interfaces and use-case logic
- `InterviewCoach.Domain` — Entity models (Session, Question, LlmRun, MetricEvent, etc.)
- `InterviewCoach.Infrastructure` — EF Core DbContext, Npgsql migrations

**Frontend structure:**
- `src/pages/` — InterviewSession, Report, ReportsList, Home, Admin
- `src/services/` — ApiService (axios), SessionTransport (batched event upload), RecordingBuffer (IndexedDB)
- `src/vision/` — MediaPipe facial/pose feature extraction
- `src/auth/` — AuthContext, ProtectedRoute, AdminRoute

**Key data flow:**
1. Session created → Questions seeded (4 static + 4 adaptive via Ollama after Q3)
2. Recording per question → audio uploaded → faster-whisper transcribes → segments stored
3. Vision events streamed every 500ms during recording → MetricEvents table
4. On finalize → ScoreCard computed → LLM coaching fired as background task
5. Report page polls `GET /api/sessions/{id}/llm/coach` every 5s until coaching appears

## Commands

### Docker (recommended)
```bash
cd docker
cp ../.env.example .env          # first time only
docker compose up -d             # start all 5 services
docker compose up -d --build api # rebuild after backend changes
docker compose logs -f api       # follow API logs
```
After first start, pull the LLM model:
```bash
docker exec -it $(docker ps -qf "name=ollama") ollama pull qwen2.5:7b-instruct
```

### Backend (.NET 8)
```bash
cd src/backend
dotnet restore
dotnet run --project InterviewCoach.Api --launch-profile http   # :8080
dotnet test InterviewCoach.sln                                   # all tests
dotnet test InterviewCoach.Api.Tests                             # API tests only
dotnet test InterviewCoach.Tests                                 # unit tests only

# Migrations
dotnet ef migrations add <Name> --project InterviewCoach.Infrastructure --startup-project InterviewCoach.Api
dotnet ef database update          --project InterviewCoach.Infrastructure --startup-project InterviewCoach.Api
```

### Frontend (React/Vite)
```bash
cd src/frontend
npm install
npm run dev      # :5173
npm run build    # tsc + vite build
npm run lint     # ESLint (max-warnings 0)
npm run e2e      # Playwright tests
npm run e2e:ui   # Playwright UI mode
```

### Speech Service (Python)
```bash
cd services/speech-service
pip install -r requirements.txt
uvicorn app.main:app --reload --port 8000
```

### Full CI check
```bash
bash scripts/test.sh   # dotnet test + npm ci + npm build
```

## Key Configuration

**`docker/.env`** is the authoritative config file. Important overrides:
- `LLM_TIMEOUT_SECONDS` — default 600 (set high; qwen2.5:7b on laptop GPU takes 5-8 min for long sessions)
- `LLM_MODEL` — default `qwen2.5:7b-instruct`
- `SPEECH_DEVICE` — `cuda` or `cpu`
- `SPEECH_MODEL` — `large-v3-turbo` (balanced) or `large-v3`

**`appsettings.json`** values are overridden by docker-compose environment variables. Pattern: `Llm__TimeoutSeconds` in compose = `Llm:TimeoutSeconds` in config.

## Database

PostgreSQL with EF Core migrations. Delete cascades are defined on all child tables (Questions, TranscriptSegments, MetricEvents, FeedbackItems, LlmRuns → Session). `BatchCoachingJobItems` has no FK to Session — must be deleted manually before Session.

Session delete sequence (SessionsController):
1. Delete `BatchCoachingJobItems` by SessionId (no cascade)
2. Delete Session → cascade handles everything else

## LLM Coaching Pipeline

- `LlmCoachingOrchestrator` — orchestrates cache lookup, model selection, retry, fallback
- `LlmCoachingService` — builds prompt + parses JSON schema response
- `LlmCoachingGuardrailsService` — quality score, PII redaction, profanity, duplicate filtering
- `LlmOptimizationService` — evidence compaction (Small/Medium/Full tier by complexity score)
- `AdaptiveQuestionService` — generates Q5-Q8 based on Q1-Q3 transcripts via Ollama

`OllamaClient` uses OpenAI-compatible `/v1/chat/completions` with `response_format: json_object`.

Coaching is triggered as fire-and-forget in `ReportController.FinalizeSession`. Frontend polls `GET /api/sessions/{id}/llm/coach` (returns 204 until ready).

## Auth

JWT Bearer. `[SessionOwnership]` filter on most session endpoints verifies `Session.UserId == currentUserId`. Roles: `User`, `Admin`.

Token stored in `localStorage` under key `interviewcoach.auth.token`.

## Frontend Interview Flow

`InterviewSession.tsx` manages the full recording lifecycle:
- Q1–Q4 use static questions; after Q4 recording ends, adaptive questions generated (blocks with loading modal)
- Audio chunks → `uploadQuestionAudio` + `transcriptionChainRef` (sequential chain prevents concurrent Whisper calls)
- Vision metrics computed per-frame, batched via `SessionTransport` every 500ms
- Live LLM analysis every 15s during recording (`analyzeLiveWindow`)
- Mülakat always ends at Q8 (`currentQuestionIndex >= 7`)
