from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from app.vad import SpeechChunkClassifier, VadDecision


def test_classifier_falls_back_to_energy_when_silero_is_unavailable(monkeypatch) -> None:
    monkeypatch.setattr("app.vad._SILERO_AVAILABLE", False)

    classifier = SpeechChunkClassifier(
        logger=__import__("logging").getLogger("speech-test"),
        primary_backend="silero",
        fallback_backend="energy",
        sample_rate=16000,
    )

    decision = classifier.classify_chunk(bytes(16000 // 4 * 2), chunk_ms=250, speech_threshold=0.01, chunk_rms=0.02)

    assert decision.backend == "energy"
    assert decision.is_speech is True
    assert decision.speech_ms == 250


def test_classifier_keeps_energy_speech_when_silero_rejects_short_chunk(monkeypatch) -> None:
    classifier = SpeechChunkClassifier(
        logger=__import__("logging").getLogger("speech-test"),
        primary_backend="silero",
        fallback_backend="energy",
        sample_rate=16000,
    )

    monkeypatch.setattr(
        classifier,
        "_classify_with_silero",
        lambda audio_bytes: VadDecision(
            is_speech=False,
            speech_ms=0,
            backend="silero",
            energy_is_speech=False,
            energy_speech_ms=0,
        ),
    )

    decision = classifier.classify_chunk(
        bytes(16000 // 4 * 2),
        chunk_ms=250,
        speech_threshold=0.01,
        chunk_rms=0.02,
        rolling_audio_bytes=bytes(16000 // 2 * 2),
        rolling_chunk_ms=500,
    )

    assert decision.backend == "energy+silero"
    assert decision.energy_is_speech is True
    assert decision.silero_is_speech is False
    assert decision.is_speech is True
    assert decision.speech_ms == 250


def test_classifier_can_rescue_soft_chunk_with_silero_confirmation(monkeypatch) -> None:
    classifier = SpeechChunkClassifier(
        logger=__import__("logging").getLogger("speech-test"),
        primary_backend="silero",
        fallback_backend="energy",
        sample_rate=16000,
    )

    monkeypatch.setattr(
        classifier,
        "_classify_with_silero",
        lambda audio_bytes: VadDecision(
            is_speech=True,
            speech_ms=420,
            backend="silero",
            energy_is_speech=False,
            energy_speech_ms=0,
            silero_is_speech=True,
            silero_speech_ms=420,
        ),
    )

    decision = classifier.classify_chunk(
        bytes(16000 // 4 * 2),
        chunk_ms=250,
        speech_threshold=0.01,
        chunk_rms=0.0068,
        rolling_audio_bytes=bytes(16000 // 2 * 2),
        rolling_chunk_ms=500,
    )

    assert decision.backend == "hybrid_silero_rescue"
    assert decision.energy_is_speech is False
    assert decision.silero_is_speech is True
    assert decision.is_speech is True
    assert decision.speech_ms == 250
