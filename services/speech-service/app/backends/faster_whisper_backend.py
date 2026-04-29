from __future__ import annotations

import asyncio
import io
import logging
import os
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
        cpu_threads: int = 4,
        num_workers: int = 1,
        download_root: str | None = None,
        strict_quality_mode: bool = True,
    ) -> None:
        self._logger = logger
        self._device = device
        self._compute_type = compute_type
        self._cpu_threads = cpu_threads
        self._num_workers = num_workers
        self._download_root = download_root
        self._strict_quality_mode = strict_quality_mode
        self._beam_size = _env_int("SPEECH_BEAM_SIZE", 1, 1, 10)
        self._best_of = _env_int("SPEECH_BEST_OF", 1, 1, 10)
        self._no_speech_threshold = _env_float("SPEECH_NO_SPEECH_THRESHOLD", 0.6, 0.0, 1.0)
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
                    lambda: _WhisperModel(
                        model_name,
                        device=self._device,
                        compute_type=self._compute_type,
                        cpu_threads=self._cpu_threads,
                        num_workers=self._num_workers,
                        download_root=self._download_root,
                    ),
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
            self._logger.info(_json_log("backend_model_warmup_start", backend="faster_whisper", model=model_name))
            try:
                await loop.run_in_executor(None, lambda: _warmup_model(model))
                self._logger.info(_json_log("backend_model_warmup_complete", backend="faster_whisper", model=model_name))
            except Exception as exc:
                self._logger.error(_json_log("backend_model_warmup_failed", backend="faster_whisper", model=model_name, error=str(exc)))
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

        del use_vad  # Segmentation is handled by the processor loop; Whisper's internal VAD is disabled.

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
                    beam_size=self._beam_size,
                    best_of=self._best_of,
                    vad_filter=False,
                    condition_on_previous_text=False,
                    no_speech_threshold=self._no_speech_threshold,
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
        kept_segments_raw: list[Any] = []
        all_words: list[str] = []
        dropped_segments = 0
        for seg in segments_raw:
            seg_text = (seg.text or "").strip()
            if not seg_text:
                continue
            if not _should_keep_segment(seg, seg_text):
                dropped_segments += 1
                continue
            kept_segments_raw.append(seg)
            all_words.extend(seg_text.split())
            print(f"\n\n🎙️ ALGILANAN KELİME: {seg_text}\n\n", flush=True)
            segments_out.append(
                {
                    "start_ms": int(start_ms + seg.start * 1000),
                    "end_ms": int(start_ms + seg.end * 1000),
                    "text": seg_text,
                }
            )

        window_suppressed = False
        if self._strict_quality_mode and _should_suppress_window(kept_segments_raw):
            dropped_segments += len(segments_out)
            segments_out = []
            all_words = []
            window_suppressed = True

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

        if dropped_segments:
            self._logger.info(
                _json_log(
                    "backend_segments_filtered",
                    backend="faster_whisper",
                    model=model_name,
                    language=language,
                    task=task,
                    kept_segments=len(segments_out),
                    dropped_segments=dropped_segments,
                )
            )

        return {
            "segments": segments_out,
            "stats": {
                "wpm": wpm,
                "filler_count": filler_count,
                "pause_count": 0,
                "pause_ms": 0,
            },
            "meta": {
                "strict_quality_mode": self._strict_quality_mode,
                "beam_size": self._beam_size,
                "best_of": self._best_of,
                "no_speech_threshold": self._no_speech_threshold,
                "filtered_segments": dropped_segments,
                "window_suppressed": window_suppressed,
                "empty_result": bool(segments_raw) and not segments_out,
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


def _warmup_model(model: Any) -> None:
    """1s sessiz ses ile CUDA kernel'larını derleyerek ilk istek latency'sini sıfırlar.
    Generator tüketilmezse CTranslate2 hiç op çalıştırmaz; list() zorunlu."""
    silent_pcm = bytes(16000 * 2)  # 1s, 16kHz mono PCM16, sıfır baytlar
    wav_bytes = _pcm16_to_wav(silent_pcm)
    wav_buffer = io.BytesIO(wav_bytes)
    segments, _ = model.transcribe(
        wav_buffer,
        language="en",
        task="transcribe",
        beam_size=5,
        vad_filter=False,
        condition_on_previous_text=False,
    )
    list(segments)


def _should_keep_segment(segment: Any, text: str) -> bool:
    words = text.split()
    word_count = len(words)
    avg_logprob = _safe_float(getattr(segment, "avg_logprob", None))
    no_speech_prob = _safe_float(getattr(segment, "no_speech_prob", None))
    compression_ratio = _safe_float(getattr(segment, "compression_ratio", None))

    # Çok belirgin sessizlik veya net halüsinasyonları engelle (Filtreler çok gevşetildi)
    if no_speech_prob >= 0.98 and word_count <= 10:
        return False

    # Güven skoru aşırı düşükse (artık sadece felaket durumlarında silecek)
    if avg_logprob <= -2.0 and word_count <= 8:
        return False

    # Yüksek sıkıştırma oranı (anlamsız tekrar eden sesler vb.)
    if compression_ratio >= 3.0 and avg_logprob <= -1.5:
        return False

    return True


def _should_suppress_window(segments: list[Any]) -> bool:
    if not segments:
        return False

    total_words = 0
    highest_no_speech = 0.0
    lowest_logprob = 0.0
    for seg in segments:
        seg_text = (getattr(seg, "text", "") or "").strip()
        word_count = len(seg_text.split())
        total_words += word_count
        no_speech_prob = _safe_float(getattr(seg, "no_speech_prob", None))
        avg_logprob = _safe_float(getattr(seg, "avg_logprob", None))
        highest_no_speech = max(highest_no_speech, no_speech_prob)
        lowest_logprob = min(lowest_logprob, avg_logprob)

    # 1-2 kelimelik kısa cevapların silinmemesi için eşikler çok esnetildi
    if total_words <= 2 and highest_no_speech >= 0.95:
        return True

    if total_words <= 4 and highest_no_speech >= 0.90 and lowest_logprob <= -1.5:
        return True

    return False


def _safe_float(value: Any) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return 0.0


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


def _env_int(name: str, default: int, min_value: int, max_value: int) -> int:
    raw = os.getenv(name)
    if raw is None:
        return default
    try:
        value = int(raw)
    except ValueError:
        return default
    return max(min_value, min(max_value, value))


def _env_float(name: str, default: float, min_value: float, max_value: float) -> float:
    raw = os.getenv(name)
    if raw is None:
        return default
    try:
        value = float(raw)
    except ValueError:
        return default
    return max(min_value, min(max_value, value))
