from __future__ import annotations

import asyncio
import io
import logging
import struct
from typing import Any

from .base import BaseAsrBackend, ModelUnavailableError, TranscriptionError, TranscriptionModelError

try:
    from faster_whisper import WhisperModel as _WhisperModel

    _FASTER_WHISPER_AVAILABLE = True
except ImportError:
    _FASTER_WHISPER_AVAILABLE = False
    _WhisperModel = None  # type: ignore


class FasterWhisperBackend(BaseAsrBackend):
    def __init__(
        self,
        *,
        logger: logging.Logger,
        device: str = "auto",
        compute_type: str = "default",
    ) -> None:
        self._logger = logger
        self._device = device
        self._compute_type = compute_type
        self._models: dict[str, Any] = {}
        self._locks: dict[str, asyncio.Lock] = {}

    @property
    def runtime_available(self) -> bool:
        return _FASTER_WHISPER_AVAILABLE

    async def load_model(self, model_name: str) -> None:
        if not self.runtime_available:
            raise ModelUnavailableError("faster_whisper is not installed.")

        if model_name in self._models:
            return

        lock = self._locks.setdefault(model_name, asyncio.Lock())
        async with lock:
            if model_name in self._models:
                return

            self._logger.info(_json_log("backend_model_load_start", backend="faster_whisper", model=model_name))
            loop = asyncio.get_running_loop()
            try:
                model = await loop.run_in_executor(
                    None,
                    lambda: _WhisperModel(model_name, device=self._device, compute_type=self._compute_type),
                )
            except Exception as exc:
                self._logger.error(
                    _json_log(
                        "backend_model_load_failed",
                        backend="faster_whisper",
                        model=model_name,
                        error=str(exc),
                    )
                )
                raise TranscriptionModelError(f"Failed to load speech model '{model_name}'.") from exc

            self._models[model_name] = model
            self._logger.info(_json_log("backend_model_load_complete", backend="faster_whisper", model=model_name))

    def is_model_ready(self, model_name: str) -> bool:
        return model_name in self._models

    async def transcribe(
        self,
        audio_bytes: bytes,
        *,
        model_name: str,
        language: str,
        task: str,
        use_vad: bool,
        start_ms: int,
        end_ms: int,
    ) -> dict[str, Any]:
        if not self.runtime_available:
            raise ModelUnavailableError("faster_whisper is not installed.")

        model = self._models.get(model_name)
        if model is None:
            await self.load_model(model_name)
            model = self._models.get(model_name)

        if model is None:
            raise ModelUnavailableError(f"Speech model '{model_name}' is not ready.")

        duration_ms = max(1, end_ms - start_ms)
        duration_sec = duration_ms / 1000.0

        if len(audio_bytes) < 2:
            return {
                "segments": [],
                "stats": {"wpm": 0, "filler_count": 0, "pause_count": 0, "pause_ms": 0},
            }

        try:
            wav_bytes = _pcm16_to_wav(audio_bytes)
            wav_buffer = io.BytesIO(wav_bytes)
            loop = asyncio.get_running_loop()

            def _run_whisper() -> tuple[list[Any], Any]:
                segments, info = model.transcribe(
                    wav_buffer,
                    language=language,
                    task=task,
                    beam_size=1,
                    vad_filter=use_vad,
                )
                return list(segments), info

            segments_raw, _info = await loop.run_in_executor(None, _run_whisper)
        except TranscriptionError:
            raise
        except Exception as exc:
            self._logger.warning(
                _json_log(
                    "backend_transcribe_error",
                    backend="faster_whisper",
                    model=model_name,
                    language=language,
                    task=task,
                    error=str(exc),
                )
            )
            raise TranscriptionModelError("Speech model failed while transcribing audio.") from exc

        segments_out: list[dict[str, Any]] = []
        all_words: list[str] = []
        for seg in segments_raw:
            seg_text = (seg.text or "").strip()
            if not seg_text:
                continue
            all_words.extend(seg_text.split())
            segments_out.append(
                {
                    "start_ms": int(start_ms + seg.start * 1000),
                    "end_ms": int(start_ms + seg.end * 1000),
                    "text": seg_text,
                }
            )

        word_count = len(all_words)
        wpm = int(round(word_count * 60.0 / max(duration_sec, 0.1))) if word_count else 0
        filler_words = {
            "um",
            "uh",
            "er",
            "ehm",
            "like",
            "you",
            "know",
            "so",
            "basically",
            "hmm",
            "ah",
            "ee",
            "mmm",
            "yani",
            "iste",
            "sey",
            "hani",
            "falan",
        }
        filler_count = sum(1 for word in all_words if word.lower().strip(".,!?") in filler_words)

        return {
            "segments": segments_out,
            "stats": {
                "wpm": wpm,
                "filler_count": filler_count,
                "pause_count": 0,
                "pause_ms": 0,
            },
        }

    async def transcribe_partial(
        self,
        audio_bytes: bytes,
        *,
        model_name: str,
        language: str,
        task: str,
        use_vad: bool,
        start_ms: int,
        end_ms: int,
    ) -> dict[str, Any]:
        result = await self.transcribe(
            audio_bytes,
            model_name=model_name,
            language=language,
            task=task,
            use_vad=use_vad,
            start_ms=start_ms,
            end_ms=end_ms,
        )
        text = " ".join(segment["text"] for segment in result["segments"]).strip()
        return {
            "text": text,
            "start_ms": start_ms,
            "end_ms": end_ms,
        }


def _pcm16_to_wav(pcm_bytes: bytes, sample_rate: int = 16000, channels: int = 1) -> bytes:
    data_size = len(pcm_bytes)
    header = struct.pack(
        "<4sI4s4sIHHIIHH4sI",
        b"RIFF",
        36 + data_size,
        b"WAVE",
        b"fmt ",
        16,
        1,
        channels,
        sample_rate,
        sample_rate * channels * 2,
        channels * 2,
        16,
        b"data",
        data_size,
    )
    return header + pcm_bytes


def _json_log(event: str, **fields: Any) -> str:
    import json

    payload: dict[str, Any] = {"event": event, **fields}
    return json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
