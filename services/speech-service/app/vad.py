from __future__ import annotations

import array
import logging
from dataclasses import dataclass

try:
    import torch
    from silero_vad import get_speech_timestamps, load_silero_vad

    _SILERO_AVAILABLE = True
except ImportError:
    _SILERO_AVAILABLE = False
    torch = None  # type: ignore[assignment]
    get_speech_timestamps = None  # type: ignore[assignment]
    load_silero_vad = None  # type: ignore[assignment]


@dataclass(frozen=True)
class VadDecision:
    is_speech: bool
    speech_ms: int
    backend: str
    energy_is_speech: bool
    energy_speech_ms: int
    silero_is_speech: bool = False
    silero_speech_ms: int = 0
    silero_window_ms: int = 0
    chunk_rms: float = 0.0
    speech_threshold: float = 0.0


class SpeechChunkClassifier:
    def __init__(
        self,
        *,
        logger: logging.Logger,
        primary_backend: str,
        fallback_backend: str,
        sample_rate: int,
    ) -> None:
        self._logger = logger
        self._primary_backend = primary_backend.strip().lower()
        self._fallback_backend = fallback_backend.strip().lower()
        self._sample_rate = sample_rate
        self._silero_model = None
        self._silero_disabled = False
        self._fallback_logged = False

    @property
    def active_backend(self) -> str:
        if self._primary_backend == "silero" and not self._silero_disabled and _SILERO_AVAILABLE:
            return "silero+energy"
        return self._fallback_backend or "energy"

    @property
    def silero_available(self) -> bool:
        return self._primary_backend == "silero" and not self._silero_disabled and _SILERO_AVAILABLE

    def classify_chunk(
        self,
        audio_bytes: bytes,
        *,
        chunk_ms: int,
        speech_threshold: float,
        chunk_rms: float,
        rolling_audio_bytes: bytes | None = None,
        rolling_chunk_ms: int | None = None,
    ) -> VadDecision:
        energy_is_speech = chunk_rms >= speech_threshold
        energy_speech_ms = chunk_ms if energy_is_speech else 0

        if self._primary_backend == "silero" and not self._silero_disabled:
            silero_window_ms = max(chunk_ms, rolling_chunk_ms or chunk_ms)
            silero_decision = self._classify_with_silero(rolling_audio_bytes or audio_bytes)
            if silero_decision is not None:
                silero_is_speech = silero_decision.speech_ms > 0
                # Keep energy as the primary per-chunk gate so trailing silence can still finalize
                # correctly; let Silero rescue softer near-threshold chunks from the rolling window.
                silero_rescue = silero_is_speech and chunk_rms >= max(0.0005, speech_threshold * 0.65)
                is_speech = energy_is_speech or silero_rescue
                backend = "hybrid_silero_rescue" if silero_rescue and not energy_is_speech else "energy+silero"
                return VadDecision(
                    is_speech=is_speech,
                    speech_ms=chunk_ms if is_speech else 0,
                    backend=backend,
                    energy_is_speech=energy_is_speech,
                    energy_speech_ms=energy_speech_ms,
                    silero_is_speech=silero_is_speech,
                    silero_speech_ms=silero_decision.speech_ms,
                    silero_window_ms=silero_window_ms,
                    chunk_rms=chunk_rms,
                    speech_threshold=speech_threshold,
                )

        return VadDecision(
            is_speech=energy_is_speech,
            speech_ms=energy_speech_ms,
            backend=self._fallback_backend or "energy",
            energy_is_speech=energy_is_speech,
            energy_speech_ms=energy_speech_ms,
            chunk_rms=chunk_rms,
            speech_threshold=speech_threshold,
        )

    def _classify_with_silero(self, audio_bytes: bytes) -> VadDecision | None:
        if not _SILERO_AVAILABLE or load_silero_vad is None or get_speech_timestamps is None or torch is None:
            self._disable_silero("silero_vad runtime is not installed")
            return None

        try:
            if self._silero_model is None:
                self._silero_model = load_silero_vad()

            audio = _pcm16_to_float_tensor(audio_bytes)
            timestamps = get_speech_timestamps(audio, self._silero_model, sampling_rate=self._sample_rate)
        except Exception as exc:
            self._disable_silero(str(exc))
            return None

        speech_samples = 0
        for item in timestamps:
            start = int(item.get("start", 0))
            end = int(item.get("end", start))
            if end > start:
                speech_samples += end - start

        speech_ms = int(round(speech_samples * 1000.0 / self._sample_rate))
        return VadDecision(
            is_speech=speech_ms > 0,
            speech_ms=speech_ms,
            backend="silero",
            energy_is_speech=False,
            energy_speech_ms=0,
            silero_is_speech=speech_ms > 0,
            silero_speech_ms=speech_ms,
        )

    def _disable_silero(self, reason: str) -> None:
        self._silero_disabled = True
        if self._fallback_logged:
            return
        self._fallback_logged = True
        self._logger.warning(
            "silero_vad_unavailable reason=%s fallback_backend=%s",
            reason,
            self._fallback_backend or "energy",
        )


def _pcm16_to_float_tensor(audio_bytes: bytes) -> "torch.Tensor":
    samples = array.array("h")
    samples.frombytes(audio_bytes)
    audio = torch.tensor(samples, dtype=torch.float32)
    return audio / 32768.0
