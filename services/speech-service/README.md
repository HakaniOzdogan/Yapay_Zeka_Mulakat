# Speech Service

FastAPI WebSocket transcription service for realtime interview sessions.

## WebSocket Contract (Backward Compatible)

- Endpoint: `ws://<host>:8000/ws/transcribe?session_id=<id>&lang=tr|en`
- Client -> Server:
  - `{"type":"audio","seq":n,"data_b64":"..."}`
  - `{"type":"end"}`
- Server -> Client:
  - `{"type":"partial","text":"...","t_ms":...}`
  - `{"type":"final","segments":[{"start_ms":...,"end_ms":...,"text":"..."}],"stats":{"wpm":...,"filler_count":...,"pause_count":...,"pause_ms":...}}`

## Hardening Features

- Concurrent connection cap via `MAX_CONCURRENT_SESSIONS`
- Per-connection inbound queue with deterministic backpressure policy
- Queue overflow policy: **drop oldest chunk** when queue is full
- Idle timeout close (`CLIENT_IDLE_TIMEOUT_SEC`)
- Transcription timeout per utterance (`TRANSCRIBE_TIMEOUT_SEC`)
- Max utterance audio buffer cap (`MAX_AUDIO_BUFFER_MS_PER_CONN`)
- Structured logs (JSON key/value payloads)
- Metrics endpoint in Prometheus text format

## Health and Metrics

- `GET /health` -> basic liveliness
- `GET /health/ready` -> readiness with fields:
  - `modelLoaded`
  - `failureReason`
  - `failureDetail`
  - `activeSessions`
  - `maxConcurrentSessions`
  - `uptimeSec`
  - Returns `503` when model is not loaded or service is at concurrent session capacity.
- `GET /metrics` -> Prometheus-style counters/gauges/summaries
  - Counters:
    - `speech_ws_connections_total`
    - `speech_ws_rejections_total`
    - `speech_ws_disconnects_total`
    - `speech_audio_chunks_received_total`
    - `speech_audio_chunks_dropped_total`
    - `speech_partial_messages_total`
    - `speech_final_messages_total`
    - `speech_transcribe_errors_total`
  - Gauges:
    - `speech_active_sessions`
    - `speech_queue_backlog_current`
  - Summaries:
    - `speech_time_to_first_partial_ms`
    - `speech_time_to_first_final_ms`
    - `speech_transcribe_latency_ms`

## Environment Variables

See `.env.example` for defaults.

Required for runtime behavior tuning:

- `MAX_CONCURRENT_SESSIONS` (default `10`)
- `MAX_QUEUE_MESSAGES_PER_CONN` (default `50`)
- `MAX_AUDIO_BUFFER_MS_PER_CONN` (default `15000`)
- `CLIENT_IDLE_TIMEOUT_SEC` (default `60`)
- `TRANSCRIBE_TIMEOUT_SEC` (default `20`)
- `VAD_SILENCE_MS` (default `850`)
- `VAD_ENERGY_THRESHOLD` (default `0.008`)
- `VAD_MIN_SPEECH_MS` (default `350`)
- `VAD_MIN_SPEECH_RATIO` (default `0.20`)
- `VAD_DYNAMIC_THRESHOLD_MULTIPLIER` (default `2.4`)
- `STREAM_DECODE_INTERVAL_MS` (default `750`)
- `STREAM_DECODE_OVERLAP_MS` (default `800`)
- `STREAM_MAX_ACTIVE_WINDOW_MS` (default `6000`)
- `STREAM_COMMIT_AGREEMENT_PASSES` (default `2`)
- `SPEECH_VAD_BACKEND` (default `silero`)
- `SPEECH_VAD_FALLBACK` (default `energy`)
- `MODEL` (default `large-v3-turbo`)
- `SPEECH_COMPUTE_TYPE` (default `int8`)
- `SPEECH_DEVICE` (default `cpu`)

## Local Run

```bash
pip install -r requirements.txt
uvicorn app.main:app --host 0.0.0.0 --port 8000
```

Test dependencies:

```bash
pip install -r tests/requirements.txt
pytest tests
```

## Tuning Notes

- Larger models and CPU-only deployments increase transcription latency.
- GPU yoksa `float16` kullanmayin. CPU varsayilani `SPEECH_COMPUTE_TYPE=int8` ve `SPEECH_DEVICE=cpu` olmalidir.
- GPU kullanilacaksa `float16` veya `int8_float16` ayarlari bilincli olarak secilmeli ve hostta NVIDIA driver/toolkit hazir olmalidir.
- If queue drops increase, reduce client chunk rate, increase worker capacity, or reduce model size.
- If readiness is healthy but final latency is high, tune `VAD_SILENCE_MS` and `MAX_AUDIO_BUFFER_MS_PER_CONN` conservatively.
- If transcript starts hallucinating on room noise, first raise `VAD_MIN_SPEECH_MS` or `VAD_MIN_SPEECH_RATIO` before raising `VAD_ENERGY_THRESHOLD`.
- Stable live transcript uses overlap + 2-pass agreement. Lower `STREAM_DECODE_INTERVAL_MS` for faster partials, or raise `STREAM_COMMIT_AGREEMENT_PASSES` for stricter commits.
- `SPEECH_VAD_BACKEND=silero` is preferred. If it cannot initialize at runtime, the service logs a warning and falls back to the adaptive energy gate.
- For long-running streams, keep `MAX_AUDIO_BUFFER_MS_PER_CONN` bounded; when exceeded, service flushes current buffer to avoid unbounded memory growth.
- `TRANSCRIBE_TIMEOUT_SEC` protects stuck transcribe calls; timeout errors are logged and session continues.
