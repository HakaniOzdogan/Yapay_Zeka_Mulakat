# AI Interview Coach - Architecture Documentation

## Executive Summary

The AI Interview Coach is a full-stack web application that provides real-time coaching and offline analysis for interview practice. Users can conduct live interviews with real-time MediaPipe vision analysis and live coaching hints, or upload recordings for batch analysis. The system evaluates performance across four key dimensions: eye contact, speaking rate, filler words, and posture. All analysis is deterministic and rule-based (no black-box ML models for scoring).

**Key Technologies:**
- **Backend:** ASP.NET Core 8, Entity Framework Core, PostgreSQL
- **Frontend:** React 18, Vite 5, TypeScript
- **ML/Vision:** MediaPipe Tasks Vision (Face/Pose Landmarker)
- **Speech Service:** FastAPI, faster-whisper, webrtcvad
- **Infrastructure:** Docker Compose, PostgreSQL 15

---

## System Architecture

### High-Level Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                        User Browser                             │
│  ┌──────────────────────────────────────┐                       │
│  │  React Frontend (Vite + TypeScript)  │                       │
│  │  - Interview Session (Real-time)     │                       │
│  │  - Offline Upload & Analysis         │                       │
│  │  - Report & Feedback Display         │                       │
│  └──────────────────────────────────────┘                       │
└─────────────────────────────────────────────────────────────────┘
                            ↓ HTTP/WebSocket
┌─────────────────────────────────────────────────────────────────┐
│                    Docker Network (appnet)                      │
│  ┌──────────────┐  ┌───────────────┐  ┌──────────────────────┐ │
│  │   Nginx/    │◄─►│.NET Backend   │◄─► PostgreSQL 15        │ │
│  │  Reverse    │  │(ASP.NET Core) │  │ (interviewcoach DB)  │ │
│  │  Proxy      │  │                │  │                      │ │
│  └──────────────┘  │ ┌────────────┐│  └──────────────────────┘ │
│                    │ │ScoringServ.││                            │
│                    │ └────────────┘│                            │
│                    └───────────────┘                            │
│                          ↓ HTTP                                 │
│                    ┌──────────────┐                             │
│                    │ FastAPI      │                             │
│                    │ Speech Srv   │                             │
│                    │ :8000        │                             │
│                    └──────────────┘                             │
└─────────────────────────────────────────────────────────────────┘
```

---

## Backend Architecture (ASP.NET Core 8)

### Clean Architecture Pattern

```
┌────────────────────────────────────────────────────────────┐
│                    API Layer (Controllers)                 │
│  SessionsController | QuestionsController | etc.          │
└────────────────────────────────────────────────────────────┘
                            ↓
┌────────────────────────────────────────────────────────────┐
│               Application Layer (Services)                 │
│  ScoringService (scoring logic)                            │
└────────────────────────────────────────────────────────────┘
                            ↓
┌────────────────────────────────────────────────────────────┐
│             Infrastructure Layer (DbContext)              │
│  ApplicationDbContext (EF Core)                            │
└────────────────────────────────────────────────────────────┘
                            ↓
┌────────────────────────────────────────────────────────────┐
│                Domain Layer (Entities)                     │
│  Session, Question, TranscriptSegment, MetricEvent, etc.  │
└────────────────────────────────────────────────────────────┘
```

### Project Structure

```
src/backend/
├── InterviewCoach.Domain/
│   ├── Session.cs              (aggregate root)
│   ├── Question.cs
│   ├── TranscriptSegment.cs
│   ├── MetricEvent.cs
│   ├── FeedbackItem.cs
│   └── ScoreCard.cs
├── InterviewCoach.Application/
│   ├── ApplicationOptions.cs    (configuration)
│   └── ScoringConfig.cs
├── InterviewCoach.Infrastructure/
│   ├── ApplicationDbContext.cs  (EF Core)
│   ├── Migrations/
│   │   ├── 20260209161633_InitialCreate.cs
│   │   └── 20260209_AddStatsJsonToSession.cs
│   └── [generated files]
├── InterviewCoach.Api/
│   ├── Controllers/
│   │   ├── SessionsController.cs
│   │   ├── QuestionsController.cs
│   │   ├── MetricsController.cs
│   │   ├── TranscriptController.cs
│   │   ├── ReportController.cs
│   │   └── ConfigController.cs
│   ├── Services/
│   │   └── ScoringService.cs
│   ├── Program.cs
│   └── InterviewCoach.Api.csproj
├── InterviewCoach.Api.Tests/
│   ├── ScoringServiceTests.cs
│   └── InterviewCoach.Api.Tests.csproj
└── Yapay_Zeka_Mulakat.sln
```

### Core Services

#### ScoringService

**Purpose:** Deterministic scoring of interview performance based on metrics and speech statistics.

**Public API:**
```csharp
public interface IScoringService
{
    ScoreCard ComputeScoreCard(Session session, List<MetricEvent> metrics, 
                               Dictionary<string, object>? stats);
    List<FeedbackItem> GenerateFeedback(Session session, ScoreCard scoreCard, 
                                        List<MetricEvent> metrics, 
                                        Dictionary<string, object>? stats);
}
```

**Scoring Formula:**
- Eye Contact (25% weight): 0-100 from vision metrics
- Speaking Rate (25% weight): Penalty-based on WPM deviation from 120-160 ideal range
- Filler Words (20% weight): Filler rate per minute (≤2→100, ≤4→scaled, >4→low)
- Posture (30% weight): Average of posture & fidget metrics from vision

**Overall = 0.25*EyeContact + 0.25*SpeakingRate + 0.20*Filler + 0.30*Posture**

#### Controllers

| Controller | Endpoints | Purpose |
|------------|-----------|---------|
| **SessionsController** | POST /api/sessions, GET /api/sessions/{id} | Session lifecycle |
| **QuestionsController** | POST seed, GET /api/sessions/{id}/questions | Question management |
| **MetricsController** | POST /api/sessions/{id}/metrics | Store real-time metrics |
| **TranscriptController** | POST /api/sessions/{id}/transcript | Store speech transcripts |
| **ReportController** | POST finalize, GET report | Score computation & reporting |
| **ConfigController** | GET /api/config | Scoring thresholds |

### Database Schema

```sql
Sessions
├── Id (PK)
├── CreatedAt
├── Status (Created/InProgress/Completed)
├── SelectedRole
├── Language
├── SettingsJson (privacy settings)
└── StatsJson (speech metrics)

Questions (FK: SessionId)
├── Id (PK)
├── SessionId (FK)
├── Order
├── Prompt

TranscriptSegments (FK: SessionId)
├── Id (PK)
├── SessionId (FK)
├── StartMs
├── EndMs
├── Text

MetricEvents (FK: SessionId)
├── Id (PK)
├── SessionId (FK)
├── TimestampMs
├── Type (combined)
└── ValueJson

FeedbackItems (FK: SessionId)
├── Id (PK)
├── SessionId (FK)
├── Category
├── Severity (1-5)
├── Title
├── Details
├── Suggestion

ScoreCards (FK: SessionId, 1-to-1)
├── Id (PK)
├── SessionId (FK)
├── EyeContactScore
├── SpeakingRateScore
├── FillerScore
├── PostureScore
└── OverallScore
```

---

## Speech Service (FastAPI)

### Architecture

```
FastAPI Application
├── Models/
│   ├── TranscribeRequest {file}
│   └── TranscribeResponse {segments, stats}
├── Services/
│   ├── transcriber.py (faster-whisper wrapper)
│   ├── audio.py (loading, normalization, RMS)
│   ├── vad.py (voice activity detection)
│   └── filler.py (Turkish/English filler detection)
└── Routes/
    ├── GET /health
    ├── POST /transcribe?language=tr&compute_stats=true
    └── POST /transcribe-chunk?language=tr (streaming)
```

### Key Endpoints

#### POST /transcribe

**Input:**
- file (audio/video blob)
- language (tr/en)
- compute_stats (true/false)

**Output:**
```json
{
  "segments": [
    {"start_ms": 0, "end_ms": 1200, "text": "..."}
  ],
  "full_text": "...",
  "duration_ms": 5000,
  "word_count": 50,
  "wpm": 600.0,
  "filler_count": 2,
  "filler_words": ["şey", "yani"],
  "pause_count": 3,
  "average_pause_ms": 150.5
}
```

### Filler Word Lists

**Turkish:** eee, ııı, şey, yani, aslında, hmm, e, ı, er, o, etc.

**English:** um, uh, like, you know, basically, actually, etc.

---

## Frontend Architecture (React + Vite)

### Component Hierarchy

```
App
├── Router
│   ├── Home Page
│   │   └── Role/Language/Mode Selector
│   │       → Session Creation
│   ├── Interview Session Page
│   │   ├── VideoCanvas (video capture + landmark drawing)
│   │   ├── LiveHints (metric display + coaching tips)
│   │   ├── TranscriptModal (transcript review)
│   │   └── Question Panel
│   ├── Offline Analyze Page
│   │   ├── File Upload
│   │   ├── Progress Bar
│   │   └── Session Info Card
│   └── Report Page
│       ├── Overall Score Card
│       ├── Metrics Grid (circular progress)
│       └── Feedback Cards (categorized feedback)
```

### Service Layer

| Service | Purpose |
|---------|---------|
| **ApiService** | RESTful calls to backend + speech-service |
| **MediaPipeService** | Face/Pose Landmarker initialization & inference |
| **MetricsComputer** | Real-time metrics from landmarks (eye contact, posture, etc.) |
| **AudioAnalyzer** | RMS energy + speech detection from audio context |
| **CoachingHints** | Rule-based hint generation (Turkish + English) |

### State Management

**Local Component State (React Hooks):**
- Session info (id, role, language, status)
- Current question index
- Video/audio playback state
- Metrics buffer
- Modal visibility

**No Redux/Context API needed** (simple enough for local state)

### Styling Architecture

```
styles/
├── index.css (global: typography, buttons, forms)
├── pages.css (page-specific: interview, offline, report)
└── App.css (layout)

Component Patterns:
- BEM-inspired naming
- Mobile-first responsive design
- CSS Grid for layouts
- Flexbox for components
```

---

## Data Flow

### Real-Time Interview Flow

```
1. User selects Role/Language/Mode → POST /api/sessions
   ↓
2. Frontend loads questions → GET /api/sessions/{id}/questions
   ↓
3. User clicks "Start Recording"
   ├─ Capture video stream from camera
   ├─ Capture audio stream from microphone
   ├─ Start MediaRecorder for audio
   └─ Initialize MediaPipe + MetricsComputer
   ↓
4. Every frame (~30fps):
   ├─ Run MediaPipe inference on video frame
   ├─ Extract face landmarks
   ├─ Compute metrics (eye contact, posture, fidget)
   └─ Buffer metrics in memory
   ↓
5. Every 1 second:
   ├─ Aggregate buffered metrics
   └─ POST /api/sessions/{id}/metrics (metrics events)
   ↓
6. User completes answer, clicks "Next Question"
   ├─ Stop MediaRecorder → get audio blob
   ├─ Upload blob to speech-service POST /transcribe
   ├─ Receive transcript segments + stats (WPM, fillers)
   ├─ Show TranscriptModal for user review
   ├─ POST /api/sessions/{id}/transcript
   └─ Proceed to next question
   ↓
7. After all questions:
   ├─ POST /api/sessions/{id}/finalize
   │  ├─ ScoringService.ComputeScoreCard() [real scores]
   │  ├─ ScoringService.GenerateFeedback() [Turkish feedback]
   │  ├─ Save scorecard + feedback to DB
   │  └─ Update session status → Completed
   ├─ GET /api/sessions/{id}/report
   └─ Display Report page with scores + feedback
```

### Offline Upload Flow

```
1. User selects Offline mode → POST /api/sessions
   ↓
2. User uploads audio/video file
   ├─ Validate file (size: <100MB, type: audio/video)
   └─ Show file details
   ↓
3. User clicks "Analyze Interview"
   ├─ Upload file to speech-service POST /transcribe
   ├─ Progress: 25%
   ├─ Receive full transcript + stats
   ├─ Progress: 50%
   ├─ POST /api/sessions/{id}/transcript + stats
   ├─ Progress: 75%
   ├─ POST /api/sessions/{id}/finalize
   │  ├─ Compute scores (from transcript stats only, no vision metrics)
   │  ├─ Generate feedback
   │  └─ Save to DB
   ├─ Progress: 100%
   └─ Navigate to Report page
```

---

## Scoring Logic Deep-Dive

### Eye Contact Score

**Input:** Average eyeContact metric from MediaPipe face landmarks

**Computation:**
```
score = normalize(eyeContact_average)  // 0-100
```

**Thresholds:**
- ≥80: Excellent eye contact
- 60-79: Good eye contact
- 40-59: Fair eye contact
- <40: Poor eye contact

### Speaking Rate Score

**Input:** WPM from speech-service transcription

**Computation:**
```
ideal_wpm = 140  // midpoint of 120-160 range
if wpm >= 120 AND wpm <= 160:
    score = 100  // perfect
elif wpm < 120:
    deviation = 120 - wpm
    score = 100 - (deviation * 0.5)  // lose ~0.5 per WPM
else:  // wpm > 160
    deviation = wpm - 160
    score = 100 - (deviation * 0.33)  // lose ~0.33 per WPM

score = max(20, score)  // minimum floor
```

**Thresholds:**
- 100: Perfect speaking rate
- 80-99: Good speaking rate
- 60-79: Fair speaking rate
- <60: Poor speaking rate

### Filler Words Score

**Input:** Filler count, duration from speech-service

**Computation:**
```
duration_minutes = duration_ms / 60000
filler_per_minute = filler_count / duration_minutes

if filler_per_minute <= 2:
    score = 100  // excellent
elif filler_per_minute <= 4:
    // scale 40 points across 2-4 range
    score = 100 - ((filler_per_minute - 2) / 2) * 40
else:  // > 4
    // further decrease
    excess = filler_per_minute - 4
    score = 50 - (excess / 2)

score = max(20, score)
```

**Thresholds:**
- 100: No/minimal fillers (≤2/min)
- 60-99: Few fillers (≤4/min)
- <60: Many fillers (>4/min)

### Posture Score

**Input:** Average of posture & fidget metrics from MediaPipe body landmarks

**Computation:**
```
avg_posture = (posture_avg + fidget_avg) / 2
score = avg_posture  // already 0-100 from MetricsComputer
```

**Thresholds:**
- ≥80: Excellent posture
- 60-79: Good posture
- 40-59: Fair posture
- <40: Poor posture

### Overall Score

**Formula:**
```
overall = (0.25 * eye_contact_score) +
          (0.25 * speaking_rate_score) +
          (0.20 * filler_score) +
          (0.30 * posture_score)

overall = clamp(overall, 0, 100)
```

**Weight Justification:**
- Eye contact (25%): Critical for communication
- Speaking rate (25%): Comprehension-related
- Filler words (20%): Professionalism marker
- Posture (30%): Largest factor, includes body language

---

## Testing Strategy

### Backend Tests (xUnit)

**ScoringServiceTests:**
- Eye contact score computation (good/default cases)
- Speaking rate score (ideal/slow/fast WPM)
- Filler score (no fillers, few, many)
- Overall score (excellent/poor/average)
- Feedback generation (categories, severity levels)
- Score boundaries (0-100 range)

**Controllers Tests (planned):**
- SessionsController (create, retrieve)
- ReportController (finalize, compute scores)
- TranscriptController (store stats)

### Frontend Tests (Jest + React Testing Library)

**Component Tests:**
- Report page (score display, feedback rendering)
- OfflineAnalyze (file upload, validation)
- VideoCanvas (frame callback triggering)

### Integration Tests

**End-to-End Flows:**
1. Real-time: Create session → ask question → record → POST metrics → finalize → report
2. Offline: Create session → upload file → transcribe → finalize → report

---

## Deployment

### Docker Compose Setup

```yaml
services:
  postgres:
    image: postgres:15
    ports: 5432
    volumes:
      - pgdata:/var/lib/postgresql/data

  backend:
    build: ./src/backend
    ports: 5000
    depends_on:
      - postgres
    env:
      - ASPNETCORE_ENVIRONMENT=Production
      - ConnectionStrings__Default=...

  frontend:
    build: ./src/frontend
    ports: 5173
    depends_on:
      - backend

  speech-service:
    build: ./services/speech-service
    ports: 8000
    depends_on:
      - backend
```

### Environment Variables

**Backend (.env):**
```
ASPNETCORE_ENVIRONMENT=Production
ConnectionStrings__Default=Host=postgres;Username=coach;Password=coachpass;Database=interviewcoach
Scoring__EyeContactGoodRatio=0.7
Scoring__HeadStabilityMaxStd=15
Scoring__WpmIdealMin=120
Scoring__WpmIdealMax=160
Scoring__FillerPerMinMax=3
Scoring__PostureLeanMax=10
```

**Frontend (.env.production):**
```
VITE_API_URL=http://localhost:5000
VITE_SPEECH_SERVICE_URL=http://localhost:8000
```

---

## Performance Considerations

### Real-Time Metrics

- **Video Inference:** MediaPipe Face/Pose Landmarker runs at ~30 FPS on modern browsers
- **Metric Buffering:** 1-second aggregation reduces API load (from 30 calls/sec to 1 call/sec)
- **Local Storage:** All metrics buffered in memory until aggregated

### Network Optimization

- **API Batching:** Metrics sent in batches (not per-frame)
- **CDN:** MediaPipe models loaded from jsdelivr (cached globally)
- **Compression:** FormData for audio/video uploads

### Database

- **Indexes:** Foreign keys indexed (MetricEvent, TranscriptSegment)
- **Pagination:** Not needed (metrics per session typically <1000 events)

---

## Privacy & Security

### Data Retention
- Raw media (video/audio) NOT stored by default
- Transcripts stored (text only)
- Metrics stored (computed values only)
- Toggle via `SettingsJson` (privacy-first)

### Authentication (Future)
- Currently: No auth (MVP)
- Recommended: JWT tokens + user identity

### Data Safety
- SSL/TLS for all network communication
- PostgreSQL encryption at rest
- CORS policy: Controlled origin

---

## Future Enhancements

### Phase 2 (Post-MVP)

1. **User Authentication**
   - Google OAuth / JWT
   - Per-user session history

2. **Practice Sessions**
   - Retry same question
   - Compare against previous attempts
   - Progress tracking

3. **Advanced Analytics**
   - Timeline charts (metrics over time)
   - Comparison reports
   - Percentile rankings

4. **ML Integration (Optional)**
   - Emotional expression detection (smile, frown)
   - Gesture analysis (hand movements)
   - Recommendation engine

5. **Accessibility**
   - Screen reader support
   - Keyboard navigation
   - High contrast mode

---

## Monitoring & Debugging

### Logging

**Backend:**
- Serilog (configured in Program.cs)
- Log levels: Information, Warning, Error

**Frontend:**
- Console.log for development
- Sentry for production errors

### Health Checks

```
GET /health
GET /api/config (verify scoring thresholds)
POST /transcribe (speech-service)
```

---

## References & Resources

- [MediaPipe Tasks Vision](https://developers.google.com/mediapipe/solutions/vision/face_landmarker)
- [faster-whisper Documentation](https://github.com/guillaumekln/faster-whisper)
- [ASP.NET Core Best Practices](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/best-practices)
- [React Interview Coaching Patterns](https://react.dev/learn)

---

**Document Version:** 1.0  
**Last Updated:** February 9, 2026  
**Maintainers:** AI Interview Coach Team
