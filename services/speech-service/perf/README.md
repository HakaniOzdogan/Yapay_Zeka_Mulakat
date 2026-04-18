# Speech-Service WebSocket Perf

Local load/soak benchmark for `ws://.../ws/transcribe` that simulates realtime audio clients.

## Prerequisites

- Python 3.10+
- Running speech-service with websocket endpoint:
  - `/ws/transcribe?session_id=...&lang=tr|en`

## Install

```bash
pip install -r services/speech-service/perf/requirements.txt
```

## Quick Run

Smoke:

```bash
python services/speech-service/perf/perf_ws_transcribe.py --scenario smoke
```

Baseline:

```bash
python services/speech-service/perf/perf_ws_transcribe.py --scenario baseline --base-url http://localhost:8000
```

Soak:

```bash
python services/speech-service/perf/perf_ws_transcribe.py --scenario soak
```

## Custom Run

```bash
python services/speech-service/perf/perf_ws_transcribe.py --clients 8 --duration-sec 45 --chunk-ms 250 --lang en --connect-timeout 8 --response-timeout 20
```

Without sending `end`:

```bash
python services/speech-service/perf/perf_ws_transcribe.py --scenario baseline --no-end
```

Optional real WAV input (must be PCM16 mono 16kHz):

```bash
python services/speech-service/perf/perf_ws_transcribe.py --scenario smoke --wav ./sample-16k-mono.wav
```

Write per-client CSV:

```bash
python services/speech-service/perf/perf_ws_transcribe.py --scenario baseline --csv
```

## CLI Arguments

- `--base-url` default `http://localhost:8000`
- `--clients` concurrent clients (overrides scenario default)
- `--duration-sec` stream duration per client (overrides scenario default)
- `--chunk-ms` pacing in ms, default `250`
- `--lang` `tr|en`
- `--scenario` `smoke|baseline|soak`
- `--connect-timeout` websocket connect timeout seconds
- `--response-timeout` max wait for server response seconds
- `--no-end` do not send `{"type":"end"}`
- `--wav` optional WAV fixture path
- `--csv` write extra CSV report

## What It Measures

- `totalClients`
- `successfulConnections`
- `failedConnections`
- `connectSuccessRate`
- `totalChunksSent`
- `chunksPerSecond`
- `partialMessagesReceived`
- `finalMessagesReceived`
- `disconnectedOrErrorCount`
- `disconnectErrorRate`
- `p50/p95` time-to-first-partial
- `p50/p95` time-to-first-final
- `errorsByType`

Also includes optional local latency estimate:
- `p50/p95PartialLocalLatencyMs` (estimated from server `t_ms` to send timestamp mapping)

## Output

JSON report:
- `artifacts/perf/speech-ws-<timestamp>.json`

Optional CSV report:
- `artifacts/perf/speech-ws-<timestamp>.csv`

## Notes

- Synthetic audio generation is deterministic and works without external files.
- Results depend on model size and host resources (CPU vs GPU, memory, load).
- If one client fails/disconnects, run continues and final summary still gets produced.
