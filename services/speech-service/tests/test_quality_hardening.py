from __future__ import annotations

import asyncio
import json
import sys
from pathlib import Path
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from app import main as speech_main
from app.backends.faster_whisper_backend import _should_keep_segment, _should_suppress_window


def test_should_keep_segment_rejects_low_confidence_short_noise() -> None:
    segment = SimpleNamespace(
        text="abone olmayi unutmayin",
        avg_logprob=-1.2,
        no_speech_prob=0.9,
        compression_ratio=2.1,
        start=0.0,
        end=1.1,
    )

    assert _should_keep_segment(segment, segment.text) is False


def test_should_suppress_window_for_short_low_confidence_segments() -> None:
    segments = [
        SimpleNamespace(
            text="abone olmayi",
            avg_logprob=-0.95,
            no_speech_prob=0.74,
            compression_ratio=2.3,
        ),
        SimpleNamespace(
            text="unutmayin",
            avg_logprob=-0.88,
            no_speech_prob=0.7,
            compression_ratio=2.4,
        ),
    ]

    assert _should_suppress_window(segments) is True


def test_health_diagnostics_exposes_quality_fields(monkeypatch) -> None:
    class _StubVad:
        active_backend = "energy"
        silero_available = False

    async def fake_active_sessions() -> int:
        return 2

    async def fake_snapshot() -> dict[str, object]:
        return {
            "counters": {
                "speech_final_messages_total": 3,
                "speech_partial_messages_total": 5,
                "speech_ws_connections_total": 1,
                "speech_transcribe_errors_total": 0,
                "speech_vad_voiced_chunks_total": 7,
                "speech_vad_rejected_chunks_total": 11,
                "filtered_decode_results_total": 4,
                "empty_decode_results_total": 2,
                "duplicate_finals_suppressed_total": 1,
            },
            "active_sessions": 2,
            "queue_backlog_current": 0,
            "partial": [],
            "final": [],
            "transcribe": [120.0, 180.0],
        }

    monkeypatch.setattr(speech_main, "_vad_classifier", _StubVad())
    monkeypatch.setattr(speech_main, "_ensure_background_model_load", lambda: asyncio.sleep(0))
    monkeypatch.setattr(speech_main.runtime, "active_sessions", fake_active_sessions)
    monkeypatch.setattr(speech_main.runtime.metrics, "snapshot", fake_snapshot)
    monkeypatch.setattr(speech_main.runtime, "model_loaded", True)
    monkeypatch.setattr(speech_main.runtime, "startup_state", "ready")
    monkeypatch.setattr(speech_main.runtime, "startup_task_started_at", 123.0)
    monkeypatch.setattr(speech_main.runtime, "model_ready_at", 456.0)
    monkeypatch.setattr(speech_main._asr_backend, "is_model_ready", lambda model_name: True)

    response = asyncio.run(speech_main.health_diagnostics())
    body = json.loads(response.body)

    assert body["silero_available"] is False
    assert body["strict_quality_mode"] is True
    assert body["audio_input_contract"] == "pcm_s16le/16000hz/mono"
    assert body["startup_state"] == "ready"
    assert body["model_loading"] is False
    assert body["live_input_sample_rate"] == 16000
    assert body["live_input_channels"] == 1
    assert body["filtered_decode_results_total"] == 4
    assert body["empty_decode_results_total"] == 2
    assert body["duplicate_finals_suppressed_total"] == 1
    assert body["upload_normalization_applied"] is False


def test_health_is_live_while_model_loading(monkeypatch) -> None:
    monkeypatch.setattr(speech_main, "_ensure_background_model_load", lambda: asyncio.sleep(0))
    monkeypatch.setattr(speech_main.runtime, "startup_state", "model_loading")
    monkeypatch.setattr(speech_main.runtime, "model_loaded", False)
    monkeypatch.setattr(speech_main._asr_backend, "is_model_ready", lambda model_name: False)

    body = asyncio.run(speech_main.health())

    assert body["status"] == "ok"
    assert body["startupState"] == "model_loading"
    assert body["modelLoaded"] is False


def test_health_ready_reports_model_loading_state(monkeypatch) -> None:
    async def fake_active_sessions() -> int:
        return 0

    monkeypatch.setattr(speech_main, "_ensure_background_model_load", lambda: asyncio.sleep(0))
    monkeypatch.setattr(speech_main.runtime, "active_sessions", fake_active_sessions)
    monkeypatch.setattr(speech_main.runtime, "startup_state", "model_loading")
    monkeypatch.setattr(speech_main.runtime, "model_loaded", False)
    monkeypatch.setattr(speech_main.runtime, "model_failure_reason", None)
    monkeypatch.setattr(speech_main.runtime, "model_failure_detail", None)
    monkeypatch.setattr(speech_main._asr_backend, "is_model_ready", lambda model_name: False)

    response = asyncio.run(speech_main.health_ready())
    body = json.loads(response.body)

    assert response.status_code == 503
    assert body["status"] == "not_ready"
    assert body["failureReason"] == "model_loading"
    assert body["startupState"] == "model_loading"


def test_health_ready_preserves_startup_failed_state(monkeypatch) -> None:
    async def fake_active_sessions() -> int:
        return 0

    monkeypatch.setattr(speech_main, "_ensure_background_model_load", lambda: asyncio.sleep(0))
    monkeypatch.setattr(speech_main.runtime, "active_sessions", fake_active_sessions)
    monkeypatch.setattr(speech_main.runtime, "startup_state", "startup_failed")
    monkeypatch.setattr(speech_main.runtime, "model_loaded", False)
    monkeypatch.setattr(speech_main.runtime, "model_failure_reason", "startup_failed")
    monkeypatch.setattr(speech_main.runtime, "model_failure_detail", "boom")
    monkeypatch.setattr(speech_main._asr_backend, "is_model_ready", lambda model_name: False)

    response = asyncio.run(speech_main.health_ready())
    body = json.loads(response.body)

    assert response.status_code == 503
    assert body["failureReason"] == "startup_failed"
    assert body["failureDetail"] == "boom"
