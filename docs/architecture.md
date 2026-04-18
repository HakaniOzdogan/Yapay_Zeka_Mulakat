# Architecture

This document explains the runtime components, storage model, core payloads, and replay workflow used in this repository.

## Components
- Frontend (`src/frontend`)
  - React + TypeScript + Vite.
  - Produces metric/transcript ingestion requests and renders reports.
- Backend (`src/backend/InterviewCoach.Api`)
  - ASP.NET Core 8 API.
  - Handles ingestion, finalization, report aggregation, evidence summary, and LLM coaching orchestration.
- Data (`src/backend/InterviewCoach.Infrastructure`)
  - EF Core + PostgreSQL.
  - Stores sessions, events, transcript, scoring outputs.
- Speech service (`services/speech-service`)
  - FastAPI service for ASR / streaming transcription.
- Ollama (`ollama` container)
  - Optional LLM runtime used by `/api/sessions/{id}/llm/coach`.

## Database Tables (Core)
- `Sessions`
- `Questions`
- `MetricEvents`
- `TranscriptSegments`
- `ScoreCards`
- `FeedbackItems`

## End-to-End Pipeline
1. Create session: `POST /api/sessions`
2. Ingest metrics: `POST /api/sessions/{sessionId}/events/batch`
3. Ingest transcripts: `POST /api/sessions/{sessionId}/transcript/batch`
4. Finalize: `POST /api/sessions/{sessionId}/finalize`
5. Consume outputs:
   - `GET /api/reports/{sessionId}`
   - `GET /api/sessions/{sessionId}/evidence-summary`
   - `POST /api/sessions/{sessionId}/llm/coach`

## Event Schema Examples

### 1) Metric Events Batch (`vision_metrics_v1`)
`POST /api/sessions/{sessionId}/events/batch`

```json
[
  {
    "clientEventId": "11111111-1111-1111-1111-111111111111",
    "tsMs": 4500,
    "source": "Vision",
    "type": "vision_metrics_v1",
    "payload": {
      "eyeContact": 0.82,
      "posture": 0.74,
      "fidget": 0.21,
      "headJitter": 0.18,
      "eyeOpenness": 0.65,
      "calibrated": true
    }
  }
]
```

### 2) Transcript Batch
`POST /api/sessions/{sessionId}/transcript/batch`

```json
[
  {
    "clientSegmentId": "22222222-2222-2222-2222-222222222222",
    "startMs": 1200,
    "endMs": 3600,
    "text": "Hello, I am ready to begin.",
    "confidence": 0.93
  }
]
```

### 3) Finalize Response (short)
`POST /api/sessions/{sessionId}/finalize`

```json
{
  "sessionId": "33333333-3333-3333-3333-333333333333",
  "scoreCard": {
    "eyeContactScore": 78,
    "speakingRateScore": 81,
    "fillerScore": 74,
    "postureScore": 76,
    "overallScore": 77
  },
  "patterns": [
    {
      "type": "audio",
      "startMs": 5000,
      "endMs": 9000,
      "severity": 3,
      "evidence": "Frequent filler usage in this range."
    }
  ],
  "derivedFeatureCount": 0
}
```

### 4) LLM Coaching JSON Schema (short)
`POST /api/sessions/{sessionId}/llm/coach`

```json
{
  "rubric": {
    "technical_correctness": 0,
    "depth": 0,
    "structure": 0,
    "clarity": 0,
    "confidence": 0
  },
  "overall": 0,
  "feedback": [
    {
      "category": "vision",
      "severity": 1,
      "title": "string",
      "evidence": "string",
      "time_range_ms": [0, 0],
      "suggestion": "string",
      "example_phrase": "string"
    }
  ],
  "drills": [
    {
      "title": "string",
      "steps": ["string"],
      "duration_min": 5
    }
  ]
}
```

## Session Replay (Debug/QA)
Replay endpoints are used to reproduce sessions deterministically and validate pipelines.

### Export
```bash
curl http://localhost:8080/api/sessions/<sessionId>/replay/export
```

### Import
```bash
curl -X POST http://localhost:8080/api/sessions/replay/import \
  -H "Content-Type: application/json" \
  --data @replay.json
```

### Run Replay (Finalize Imported Session)
```bash
curl -X POST http://localhost:8080/api/sessions/<newSessionId>/replay/run \
  -H "Content-Type: application/json" \
  -d '{"speed":1.0}'
```

### Typical Use Cases
- Reproduce a bug with exact event/transcript inputs.
- Compare scoring/coaching output before and after backend changes.
- Build deterministic QA fixtures for regression testing.