"""Unified audio processor — VAD + decode orchestration in a single loop.

Replaces the old 3-loop (receiver → processor → transcriber) pipeline with a
direct 2-loop design (receiver → processor/transcriber merged) to cut queuing
latency in half.
"""

from __future__ import annotations

import asyncio
import logging
import math
import time
from dataclasses import dataclass, field
from typing import Any, Callable, Awaitable

from .backends.base import BaseAsrBackend, TranscriptionError
from .streaming_state import (
    StreamingCommitState,
    CommitUpdate,
    HypothesisSegment,
    apply_decode_result,
    build_hypothesis_segments,
)
from .vad import SpeechChunkClassifier, VadDecision


SAMPLE_RATE = 16_000
BYTES_PER_SAMPLE = 2
BYTES_PER_SECOND = SAMPLE_RATE * BYTES_PER_SAMPLE


@dataclass
class ProcessorConfig:
    """All tuneable knobs for the audio processor."""

    vad_silence_ms: int = 500
    vad_energy_threshold: float = 0.006
    vad_min_speech_ms: int = 250
    vad_min_speech_ratio: float = 0.25
    vad_dynamic_threshold_multiplier: float = 2.4
    decode_interval_ms: int = 300
    decode_overlap_ms: int = 400
    max_active_window_ms: int = 5000
    commit_agreement_passes: int = 1
    max_buffer_ms: int = 15000
    transcribe_timeout_sec: int = 15


@dataclass
class AudioWindow:
    """Sliding window of raw PCM audio being accumulated for transcription."""

    audio: bytearray = field(default_factory=bytearray)
    start_ms: int = 0
    duration_ms: int = 0
    voiced_ms: int = 0
    pending_voiced_ms: int = 0
    trailing_silence_ms: int = 0
    speech_started: bool = False

    def reset(self) -> None:
        self.audio.clear()
        self.start_ms = 0
        self.duration_ms = 0
        self.voiced_ms = 0
        self.pending_voiced_ms = 0
        self.trailing_silence_ms = 0
        self.speech_started = False

    def append(self, pcm: bytes, chunk_ms: int, *, is_voiced: bool, total_ms: int) -> None:
        if not self.speech_started:
            self.speech_started = True
            self.start_ms = max(0, total_ms - chunk_ms)
        self.audio.extend(pcm)
        self.duration_ms += chunk_ms
        if is_voiced:
            self.voiced_ms += chunk_ms
            self.pending_voiced_ms += chunk_ms

    @property
    def end_ms(self) -> int:
        return self.start_ms + self.duration_ms

    @property
    def has_audio(self) -> bool:
        return self.duration_ms > 0 and len(self.audio) > 0


# -- Callbacks for the processor to communicate results back to the WS handler --

SendPartial = Callable[[str, int], Awaitable[None]]
SendFinal = Callable[[list[dict], dict], Awaitable[None]]
SendNotice = Callable[[str], Awaitable[None]]


class AudioProcessor:
    """Unified audio ingest + VAD + decode orchestrator.

    Designed to be created per-session.  Call ``feed_chunk`` for every incoming
    audio chunk and ``finalize`` when the client sends ``end``.
    """

    def __init__(
        self,
        *,
        cfg: ProcessorConfig,
        asr: BaseAsrBackend,
        vad: SpeechChunkClassifier,
        model_name: str,
        language: str,
        task: str,
        use_vad: bool,
        logger: logging.Logger,
        send_partial: SendPartial,
        send_final: SendFinal,
        send_notice: SendNotice,
    ) -> None:
        self._cfg = cfg
        self._asr = asr
        self._vad = vad
        self._model_name = model_name
        self._language = language
        self._task = task
        self._use_vad = use_vad
        self._log = logger

        # Callbacks
        self._send_partial = send_partial
        self._send_final = send_final
        self._send_notice = send_notice

        # State
        self._window = AudioWindow()
        self._commit = StreamingCommitState()
        self._total_audio_ms = 0
        self._noise_floor = max(0.0005, cfg.vad_energy_threshold * 0.5)
        self._decode_running: asyncio.Task[None] | None = None
        self._last_partial_text = ""
        self._last_partial_t_ms = 0
        self._last_final_sig = ""
        self._vad_history = bytearray()
        self._vad_history_ms = 0
        self._conn_started = time.monotonic()
        self._first_partial_sent = False
        self._first_final_sent = False

        # Metrics
        self.decode_count = 0
        self.partial_count = 0
        self.final_count = 0

    async def feed_chunk(self, pcm: bytes, chunk_ms: int) -> None:
        """Process one incoming PCM-16 chunk from the client."""

        self._total_audio_ms += chunk_ms

        # --- VAD ---
        chunk_rms = _pcm16_rms(pcm)
        threshold = _resolve_speech_threshold(
            self._cfg.vad_energy_threshold,
            self._noise_floor,
            self._cfg.vad_dynamic_threshold_multiplier,
        )

        if self._use_vad:
            # Maintain a rolling window for Silero
            self._vad_history_ms = _append_rolling(
                self._vad_history, self._vad_history_ms,
                pcm, chunk_ms,
                max_window_ms=max(1000, self._cfg.decode_interval_ms),
            )
            decision = self._vad.classify_chunk(
                pcm,
                chunk_ms=chunk_ms,
                speech_threshold=threshold,
                chunk_rms=chunk_rms,
                rolling_audio_bytes=bytes(self._vad_history),
                rolling_chunk_ms=self._vad_history_ms,
            )
        else:
            decision = VadDecision(
                is_speech=True,
                speech_ms=chunk_ms,
                backend="disabled",
                energy_is_speech=True,
                energy_speech_ms=chunk_ms,
                chunk_rms=chunk_rms,
                speech_threshold=threshold,
            )

        is_voiced = decision.is_speech

        # --- Pre-speech silence: update noise floor, skip ---
        if not self._window.speech_started and not is_voiced:
            if self._use_vad:
                self._noise_floor = _update_noise_floor(self._noise_floor, chunk_rms)
            return

        # --- Append audio to window ---
        self._window.append(pcm, chunk_ms, is_voiced=is_voiced, total_ms=self._total_audio_ms)

        # Trailing silence tracking
        if self._use_vad and not is_voiced:
            self._window.trailing_silence_ms += chunk_ms
            self._noise_floor = _update_noise_floor(self._noise_floor, chunk_rms)
        else:
            self._window.trailing_silence_ms = 0

        # --- Trim window if too large ---
        self._trim_window()

        # --- Buffer overflow guard ---
        if self._window.duration_ms > self._cfg.max_buffer_ms:
            self._log.warning("audio_buffer_overflow duration_ms=%d", self._window.duration_ms)
            await self._dispatch_decode(force_finalize=True, reason="buffer_overflow")
            self._window.reset()
            return

        # --- Trigger decode based on voiced accumulation ---
        if self._window.pending_voiced_ms >= self._cfg.decode_interval_ms:
            await self._dispatch_decode(force_finalize=False, reason="interval")

        # --- Trigger finalize on silence ---
        if (
            self._use_vad
            and self._window.trailing_silence_ms >= self._cfg.vad_silence_ms
            and self._window.has_audio
        ):
            await self._dispatch_decode(force_finalize=True, reason="vad_silence")
            self._window.reset()

    async def finalize(self) -> None:
        """Called when the client sends an ``end`` message."""
        if self._decode_running and not self._decode_running.done():
            await self._decode_running
        if self._window.has_audio:
            await self._run_decode(force_finalize=True, reason="client_end")
            self._window.reset()

    # ------------------------------------------------------------------
    # Internal decode pipeline
    # ------------------------------------------------------------------

    async def _dispatch_decode(self, *, force_finalize: bool, reason: str) -> None:
        """Fire a decode job.  If a previous job is still running, await it first."""
        # Wait for previous decode to finish (simple back-pressure)
        if self._decode_running and not self._decode_running.done():
            try:
                await asyncio.wait_for(self._decode_running, timeout=self._cfg.transcribe_timeout_sec)
            except asyncio.TimeoutError:
                self._log.warning("previous decode timed out, skipping")

        await self._run_decode(force_finalize=force_finalize, reason=reason)
        self._window.pending_voiced_ms = 0

    async def _run_decode(self, *, force_finalize: bool, reason: str) -> None:
        if not self._window.has_audio:
            return

        if not force_finalize and not _has_meaningful_speech(
            self._window.duration_ms, self._window.voiced_ms, self._cfg
        ):
            return

        audio_snap = bytes(self._window.audio)
        start_ms = self._window.start_ms
        end_ms = self._window.end_ms

        t0 = time.monotonic()
        try:
            result = await asyncio.wait_for(
                self._asr.transcribe(
                    audio_snap,
                    model_name=self._model_name,
                    language=self._language,
                    task=self._task,
                    use_vad=False,
                    start_ms=start_ms,
                    end_ms=end_ms,
                ),
                timeout=self._cfg.transcribe_timeout_sec,
            )
        except asyncio.TimeoutError:
            self._log.warning("transcribe_timeout reason=%s", reason)
            return
        except TranscriptionError as exc:
            self._log.warning("transcribe_error reason=%s error=%s", reason, exc.detail)
            return

        latency_ms = (time.monotonic() - t0) * 1000.0
        self.decode_count += 1
        self._log.info(
            "decode reason=%s latency=%.0fms segments=%d force=%s",
            reason, latency_ms, len(result.get("segments", [])), force_finalize,
        )

        # --- Apply streaming state machine ---
        segments_raw = result.get("segments", [])
        hypothesis = build_hypothesis_segments(
            segments_raw,
            last_committed_end_ms=self._commit.last_committed_end_ms,
            committed_segments=self._commit.committed_segments,
        )
        update = apply_decode_result(
            self._commit,
            hypothesis,
            force_finalize=force_finalize,
            agreement_passes=self._cfg.commit_agreement_passes,
        )

        # --- Emit final ---
        if update.final_segments:
            sig = _segment_signature(update.final_segments)
            if sig != self._last_final_sig:
                self._last_final_sig = sig
                await self._send_final(
                    [s.to_payload() for s in update.final_segments],
                    result.get("stats", {}),
                )
                self.final_count += 1
                if not self._first_final_sent:
                    self._first_final_sent = True
                    self._log.info(
                        "first_final elapsed=%.0fms",
                        (time.monotonic() - self._conn_started) * 1000,
                    )

        # --- Emit partial ---
        partial_text = " ".join(s.text for s in update.partial_segments).strip()
        partial_t = update.partial_segments[-1].end_ms if update.partial_segments else end_ms

        if partial_text:
            if partial_text != self._last_partial_text or partial_t > self._last_partial_t_ms:
                await self._send_partial(partial_text, partial_t)
                self._last_partial_text = partial_text
                self._last_partial_t_ms = partial_t
                self.partial_count += 1
                if not self._first_partial_sent:
                    self._first_partial_sent = True
                    self._log.info(
                        "first_partial elapsed=%.0fms",
                        (time.monotonic() - self._conn_started) * 1000,
                    )
        elif self._last_partial_text:
            await self._send_partial("", partial_t)
            self._last_partial_text = ""
            self._last_partial_t_ms = partial_t

    # ------------------------------------------------------------------
    # Helpers
    # ------------------------------------------------------------------

    def _trim_window(self) -> None:
        max_ms = self._cfg.max_active_window_ms
        if self._window.duration_ms <= max_ms:
            return
        overlap = self._cfg.decode_overlap_ms
        max_trim = max(0, self._window.duration_ms - overlap)
        trim_ms = min(self._window.duration_ms - max_ms, max_trim)
        if trim_ms <= 0:
            return
        trim_bytes = _ms_to_bytes(trim_ms)
        del self._window.audio[:trim_bytes]
        self._window.duration_ms = max(0, self._window.duration_ms - trim_ms)
        self._window.voiced_ms = max(0, self._window.voiced_ms - min(self._window.voiced_ms, trim_ms))
        self._window.start_ms += trim_ms


# ======================================================================
# Pure helper functions
# ======================================================================


def _pcm16_rms(audio_bytes: bytes) -> float:
    if len(audio_bytes) < 2:
        return 0.0
    sample_count = len(audio_bytes) // 2
    if sample_count == 0:
        return 0.0
    total = 0.0
    for i in range(0, sample_count * 2, 2):
        value = int.from_bytes(audio_bytes[i : i + 2], byteorder="little", signed=True)
        normalized = value / 32768.0
        total += normalized * normalized
    return math.sqrt(total / sample_count)


def _resolve_speech_threshold(base: float, noise_floor: float, multiplier: float) -> float:
    return max(base, noise_floor * multiplier)


def _update_noise_floor(current: float, observed: float) -> float:
    smoothed = (current * 0.92) + (observed * 0.08)
    return max(0.0005, smoothed)


def _has_meaningful_speech(window_ms: int, voiced_ms: int, cfg: ProcessorConfig) -> bool:
    if window_ms <= 0:
        return False
    ratio = voiced_ms / window_ms
    return voiced_ms >= min(window_ms, cfg.vad_min_speech_ms) and ratio >= cfg.vad_min_speech_ratio


def _ms_to_bytes(ms: int) -> int:
    raw = int(round((ms / 1000.0) * BYTES_PER_SECOND))
    if raw % 2 == 1:
        raw += 1
    return max(0, raw)


def _append_rolling(
    buf: bytearray, current_ms: int,
    pcm: bytes, chunk_ms: int,
    *, max_window_ms: int,
) -> int:
    buf.extend(pcm)
    current_ms += chunk_ms
    if current_ms <= max_window_ms:
        return current_ms
    trim_ms = current_ms - max_window_ms
    trim_bytes = _ms_to_bytes(trim_ms)
    if trim_bytes > 0:
        del buf[:trim_bytes]
    return max(0, current_ms - trim_ms)


def _segment_signature(segments: list[HypothesisSegment]) -> str:
    parts: list[str] = []
    for s in segments:
        text = " ".join(s.text.strip().lower().split())
        if text:
            parts.append(f"{s.start_ms}:{s.end_ms}:{text}")
    return "|".join(parts)
