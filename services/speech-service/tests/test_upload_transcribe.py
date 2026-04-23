from __future__ import annotations

import asyncio
import io
import json
import sys
from pathlib import Path

from starlette.datastructures import Headers, UploadFile

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from app import main as speech_main


def test_transcribe_upload_normalizes_audio_and_updates_diagnostics(monkeypatch) -> None:
    async def fake_probe(payload: bytes, *, filename: str | None, content_type: str | None) -> speech_main.UploadAudioMetadata:
        assert payload == b"encoded-webm"
        assert filename == "answer.webm"
        assert content_type == "audio/webm"
        return speech_main.UploadAudioMetadata(
            container="matroska,webm",
            codec="opus",
            sample_rate=48000,
            channels=2,
            content_type=content_type,
            filename=filename,
        )

    async def fake_normalize(payload: bytes, *, filename: str | None) -> bytes:
        assert payload == b"encoded-webm"
        assert filename == "answer.webm"
        return b"\x01\x00" * 16000

    async def fake_transcribe(
        audio_bytes: bytes,
        *,
        model_name: str,
        language: str,
        task: str,
        use_vad: bool,
        start_ms: int,
        end_ms: int,
    ) -> dict[str, object]:
        assert audio_bytes == b"\x01\x00" * 16000
        assert model_name == speech_main.config.model_name
        assert language == "tr"
        assert task == "transcribe"
        assert use_vad is False
        assert start_ms == 0
        assert end_ms == 1000
        return {
            "segments": [{"start_ms": 0, "end_ms": 1000, "text": "merhaba dunya"}],
            "stats": {"wpm": 120, "filler_count": 0, "pause_count": 0, "pause_ms": 0},
            "meta": {"filtered_segments": 0, "window_suppressed": False, "empty_result": False},
        }

    monkeypatch.setattr(speech_main, "_probe_upload_audio_metadata", fake_probe)
    monkeypatch.setattr(speech_main, "_normalize_upload_to_pcm16", fake_normalize)
    monkeypatch.setattr(speech_main._asr_backend, "transcribe", fake_transcribe)
    monkeypatch.setattr(speech_main._asr_backend, "is_model_ready", lambda model_name: True)
    monkeypatch.setattr(speech_main.runtime, "model_loaded", True)

    upload = UploadFile(
        file=io.BytesIO(b"encoded-webm"),
        filename="answer.webm",
        headers=Headers({"content-type": "audio/webm"}),
    )

    response = asyncio.run(speech_main.transcribe_upload(file=upload, language="tr", compute_stats=True))
    body = json.loads(response.body)

    assert body["full_text"] == "merhaba dunya"
    assert body["duration_ms"] == 1000
    assert body["word_count"] == 2
    assert body["audio_format"]["input_contract"] == "pcm_s16le/16000hz/mono"
    assert body["audio_format"]["upload_codec"] == "opus"
    assert body["audio_format"]["normalization_applied"] is True

    diagnostics = json.loads(asyncio.run(speech_main.health_diagnostics()).body)
    assert diagnostics["last_upload_container"] == "matroska,webm"
    assert diagnostics["last_upload_codec"] == "opus"
    assert diagnostics["last_upload_sample_rate"] == 48000
    assert diagnostics["last_upload_channels"] == 2
    assert diagnostics["upload_normalization_applied"] is True


def test_transcribe_upload_rejects_unsupported_language(monkeypatch) -> None:
    monkeypatch.setattr(speech_main._asr_backend, "is_model_ready", lambda model_name: True)
    monkeypatch.setattr(speech_main.runtime, "model_loaded", True)

    upload = UploadFile(
        file=io.BytesIO(b"encoded-webm"),
        filename="answer.webm",
        headers=Headers({"content-type": "audio/webm"}),
    )

    try:
        asyncio.run(speech_main.transcribe_upload(file=upload, language="de", compute_stats=False))
    except speech_main.HTTPException as exc:
        assert exc.status_code == 400
        assert "Unsupported language" in str(exc.detail)
    else:
        raise AssertionError("Expected HTTPException for unsupported language")
