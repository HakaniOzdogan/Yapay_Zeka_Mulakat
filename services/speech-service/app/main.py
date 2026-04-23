from __future__ import annotations

import asyncio
import base64
import binascii
import json
import logging
import math
import os
import tempfile
import time
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from fastapi import FastAPI, File, HTTPException, Query, UploadFile, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse, PlainTextResponse
from starlette.websockets import WebSocketState

from .backends import FasterWhisperBackend, TranscriptionError
from .protocol import ClientAudioMessage, ClientConfigMessage, ClientEndMessage, ProtocolError, parse_client_message
from .session import TranscriptionSession
from .streaming_state import apply_decode_result, build_hypothesis_segments
from .vad import SpeechChunkClassifier, VadDecision

SAMPLE_RATE = 16_000
BYTES_PER_SAMPLE = 2
BYTES_PER_SECOND = SAMPLE_RATE * BYTES_PER_SAMPLE
AUDIO_INPUT_CONTRACT = "pcm_s16le/16000hz/mono"
LIVE_INPUT_CHUNK_MS = 250


@dataclass(frozen=True)
class ServiceConfig:
    max_concurrent_sessions: int
    max_queue_messages_per_conn: int
    max_audio_buffer_ms_per_conn: int
    client_idle_timeout_sec: int
    backpressure_queue_wait_ms: int
    transcribe_timeout_sec: int
    vad_silence_ms: int
    vad_energy_threshold: float
    vad_min_speech_ms: int
    vad_min_speech_ratio: float
    vad_dynamic_threshold_multiplier: float
    stream_decode_interval_ms: int
    stream_decode_overlap_ms: int
    stream_max_active_window_ms: int
    stream_commit_agreement_passes: int
    speech_vad_backend: str
    speech_vad_fallback: str
    strict_quality_mode: bool
    model_name: str
    cpu_threads: int
    num_workers: int

    @staticmethod
    def from_env() -> "ServiceConfig":
        return ServiceConfig(
            max_concurrent_sessions=_env_int("MAX_CONCURRENT_SESSIONS", 10, 1, 1000),
            max_queue_messages_per_conn=_env_int("MAX_QUEUE_MESSAGES_PER_CONN", 50, 1, 5000),
            max_audio_buffer_ms_per_conn=_env_int("MAX_AUDIO_BUFFER_MS_PER_CONN", 15000, 1000, 300000),
            client_idle_timeout_sec=_env_int("CLIENT_IDLE_TIMEOUT_SEC", 60, 5, 300),
            backpressure_queue_wait_ms=_env_int("BACKPRESSURE_QUEUE_WAIT_MS", 1000, 100, 10000),
            transcribe_timeout_sec=_env_int("TRANSCRIBE_TIMEOUT_SEC", 20, 1, 300),
            vad_silence_ms=_env_int("VAD_SILENCE_MS", 700, 200, 5000),
            vad_energy_threshold=_env_float("VAD_ENERGY_THRESHOLD", 0.008, 0.0001, 1.0),
            vad_min_speech_ms=_env_int("VAD_MIN_SPEECH_MS", 400, 100, 5000),
            vad_min_speech_ratio=_env_float("VAD_MIN_SPEECH_RATIO", 0.35, 0.05, 1.0),
            vad_dynamic_threshold_multiplier=_env_float("VAD_DYNAMIC_THRESHOLD_MULTIPLIER", 2.4, 1.0, 10.0),
            stream_decode_interval_ms=_env_int("STREAM_DECODE_INTERVAL_MS", 650, 200, 10000),
            stream_decode_overlap_ms=_env_int("STREAM_DECODE_OVERLAP_MS", 800, 0, 5000),
            stream_max_active_window_ms=_env_int("STREAM_MAX_ACTIVE_WINDOW_MS", 6000, 1000, 30000),
            stream_commit_agreement_passes=_env_int("STREAM_COMMIT_AGREEMENT_PASSES", 2, 1, 5),
            speech_vad_backend=os.getenv("SPEECH_VAD_BACKEND", "silero").strip().lower() or "silero",
            speech_vad_fallback=os.getenv("SPEECH_VAD_FALLBACK", "energy").strip().lower() or "energy",
            strict_quality_mode=_env_bool("STRICT_QUALITY_MODE", True),
            model_name=os.getenv("MODEL", "small"),
            cpu_threads=_env_int("SPEECH_CPU_THREADS", 8, 1, 32),
            num_workers=_env_int("SPEECH_NUM_WORKERS", 1, 1, 8),
        )


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


def _env_bool(name: str, default: bool) -> bool:
    raw = os.getenv(name)
    if raw is None:
        return default
    normalized = raw.strip().lower()
    if normalized in {"1", "true", "yes", "on"}:
        return True
    if normalized in {"0", "false", "no", "off"}:
        return False
    return default


def _json_log(event: str, **fields: Any) -> str:
    payload: dict[str, Any] = {"event": event, **fields}
    return json.dumps(payload, ensure_ascii=False, separators=(",", ":"))


@dataclass(frozen=True)
class UploadAudioMetadata:
    container: str | None = None
    codec: str | None = None
    sample_rate: int | None = None
    channels: int | None = None
    content_type: str | None = None
    filename: str | None = None
    normalization_applied: bool = False


class MetricsRegistry:
    def __init__(self) -> None:
        self._lock = asyncio.Lock()
        self.counters: dict[str, int] = {
            "speech_ws_connections_total": 0,
            "speech_ws_rejections_total": 0,
            "speech_ws_disconnects_total": 0,
            "speech_audio_chunks_received_total": 0,
            "speech_audio_chunks_dropped_total": 0,
            "speech_audio_decode_errors_total": 0,
            "speech_backpressure_events_total": 0,
            "speech_partial_status_messages_total": 0,
            "speech_partial_messages_total": 0,
            "speech_final_messages_total": 0,
            "speech_vad_voiced_chunks_total": 0,
            "speech_vad_rejected_chunks_total": 0,
            "speech_decode_jobs_queued_total": 0,
            "speech_streams_stalled_total": 0,
            "speech_commit_prefix_promotions_total": 0,
            "speech_finalize_carry_over_total": 0,
            "speech_finalize_empty_after_partials_total": 0,
            "filtered_decode_results_total": 0,
            "empty_decode_results_total": 0,
            "duplicate_finals_suppressed_total": 0,
            "speech_transcribe_errors_total": 0,
            "speech_transcribe_timeouts_total": 0,
            "speech_transcribe_model_errors_total": 0,
        }
        self.active_sessions = 0
        self.queue_backlog_current = 0
        self.time_to_first_partial_ms: list[float] = []
        self.time_to_first_final_ms: list[float] = []
        self.transcribe_latency_ms: list[float] = []

    async def inc(self, name: str, value: int = 1) -> None:
        async with self._lock:
            self.counters[name] = self.counters.get(name, 0) + value

    async def set_active_sessions(self, value: int) -> None:
        async with self._lock:
            self.active_sessions = value

    async def set_queue_backlog(self, value: int) -> None:
        async with self._lock:
            self.queue_backlog_current = value

    async def observe_partial_latency(self, ms: float) -> None:
        async with self._lock:
            self.time_to_first_partial_ms.append(ms)
            if len(self.time_to_first_partial_ms) > 10000:
                self.time_to_first_partial_ms = self.time_to_first_partial_ms[-10000:]

    async def observe_final_latency(self, ms: float) -> None:
        async with self._lock:
            self.time_to_first_final_ms.append(ms)
            if len(self.time_to_first_final_ms) > 10000:
                self.time_to_first_final_ms = self.time_to_first_final_ms[-10000:]

    async def observe_transcribe_latency(self, ms: float) -> None:
        async with self._lock:
            self.transcribe_latency_ms.append(ms)
            if len(self.transcribe_latency_ms) > 10000:
                self.transcribe_latency_ms = self.transcribe_latency_ms[-10000:]

    async def snapshot(self) -> dict[str, Any]:
        async with self._lock:
            return {
                "counters": dict(self.counters),
                "active_sessions": self.active_sessions,
                "queue_backlog_current": self.queue_backlog_current,
                "partial": list(self.time_to_first_partial_ms),
                "final": list(self.time_to_first_final_ms),
                "transcribe": list(self.transcribe_latency_ms),
            }


class RuntimeState:
    def __init__(self, config: ServiceConfig) -> None:
        self.config = config
        self.start_monotonic = time.monotonic()
        self.model_loaded = False
        self.startup_state = "starting"
        self.startup_task_started_at: float | None = None
        self.model_ready_at: float | None = None
        self.model_failure_reason: str | None = None
        self.model_failure_detail: str | None = None
        self.metrics = MetricsRegistry()
        self._sessions_lock = asyncio.Lock()
        self._startup_lock = asyncio.Lock()
        self._upload_lock = asyncio.Lock()
        self._active_connection_ids: set[str] = set()
        self._connection_backlogs: dict[str, int] = {}
        self._last_upload_metadata = UploadAudioMetadata()
        self._model_load_task: asyncio.Task[None] | None = None

    async def mark_model_loading(self) -> None:
        self.model_loaded = False
        self.startup_state = "model_loading"
        self.startup_task_started_at = time.time()
        self.model_ready_at = None
        self.model_failure_reason = None
        self.model_failure_detail = None

    async def mark_model_loaded(self, loaded: bool) -> None:
        self.model_loaded = loaded
        if loaded:
            self.startup_state = "ready"
            self.model_ready_at = time.time()
            self.model_failure_reason = None
            self.model_failure_detail = None

    async def mark_model_failed(self, detail: str, reason: str = "startup_failed") -> None:
        self.model_loaded = False
        self.startup_state = "startup_failed"
        self.model_failure_reason = reason
        self.model_failure_detail = detail

    async def try_acquire_session(self, connection_id: str) -> bool:
        async with self._sessions_lock:
            if len(self._active_connection_ids) >= self.config.max_concurrent_sessions:
                return False
            self._active_connection_ids.add(connection_id)
            await self.metrics.set_active_sessions(len(self._active_connection_ids))
            return True

    async def release_session(self, connection_id: str) -> None:
        async with self._sessions_lock:
            self._active_connection_ids.discard(connection_id)
            self._connection_backlogs.pop(connection_id, None)
            backlog = sum(self._connection_backlogs.values())
            await self.metrics.set_active_sessions(len(self._active_connection_ids))
            await self.metrics.set_queue_backlog(backlog)

    async def update_backlog(self, connection_id: str, qsize: int) -> None:
        async with self._sessions_lock:
            self._connection_backlogs[connection_id] = qsize
            await self.metrics.set_queue_backlog(sum(self._connection_backlogs.values()))

    def uptime_sec(self) -> int:
        return int(time.monotonic() - self.start_monotonic)

    async def active_sessions(self) -> int:
        async with self._sessions_lock:
            return len(self._active_connection_ids)

    async def record_upload_metadata(self, metadata: UploadAudioMetadata) -> None:
        async with self._upload_lock:
            self._last_upload_metadata = metadata

    async def get_upload_metadata(self) -> UploadAudioMetadata:
        async with self._upload_lock:
            return self._last_upload_metadata

    async def get_model_load_task(self) -> asyncio.Task[None] | None:
        async with self._startup_lock:
            return self._model_load_task

    async def set_model_load_task(self, task: asyncio.Task[None] | None) -> None:
        async with self._startup_lock:
            self._model_load_task = task


@dataclass
class InboundChunk:
    kind: str
    seq: int | None = None
    audio_bytes: bytes | None = None
    chunk_ms: int = 0


@dataclass(frozen=True)
class TranscribeJob:
    audio_bytes: bytes
    start_ms: int
    end_ms: int
    reason: str
    force_finalize: bool = False


config = ServiceConfig.from_env()
runtime = RuntimeState(config)

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("speech-service")

app = FastAPI(title="speech-service")
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=False,
    allow_methods=["*"],
    allow_headers=["*"],
)

_asr_backend = FasterWhisperBackend(
    logger=logger,
    device=os.getenv("SPEECH_DEVICE", "cpu"),
    compute_type=os.getenv("SPEECH_COMPUTE_TYPE", "int8"),
    cpu_threads=config.cpu_threads,
    num_workers=config.num_workers,
    download_root=None,  # HF default: /root/.cache/huggingface (volume mount)
    strict_quality_mode=config.strict_quality_mode,
)
_vad_classifier = SpeechChunkClassifier(
    logger=logger,
    primary_backend=config.speech_vad_backend,
    fallback_backend=config.speech_vad_fallback,
    sample_rate=SAMPLE_RATE,
)


async def _ensure_background_model_load() -> None:
    async with runtime._startup_lock:
        existing_task = runtime._model_load_task
        if existing_task is not None and not existing_task.done():
            return

        if runtime.model_loaded and _asr_backend.is_model_ready(config.model_name):
            return

        if runtime.startup_state == "startup_failed":
            return

        if not _asr_backend.runtime_available:
            detail = "faster_whisper is not installed."
            await runtime.mark_model_failed(detail)
            logger.warning(_json_log("whisper_unavailable", reason=detail))
            return

        await runtime.mark_model_loading()
        task = asyncio.create_task(_load_default_model_background(), name="speech-model-loader")
        runtime._model_load_task = task


async def _load_default_model_background() -> None:
    try:
        logger.info(_json_log("whisper_load_start", model=config.model_name, backend="faster_whisper"))
        await _asr_backend.load_model(config.model_name)
        logger.info(_json_log("whisper_load_complete", model=config.model_name, backend="faster_whisper"))
        await runtime.mark_model_loaded(_asr_backend.is_model_ready(config.model_name))
    except asyncio.CancelledError:
        logger.info(_json_log("whisper_load_cancelled", model=config.model_name, backend="faster_whisper"))
        raise
    except TranscriptionError as exc:
        await runtime.mark_model_failed(exc.detail)
        logger.error(_json_log("whisper_load_failed", model=config.model_name, backend="faster_whisper", error=exc.detail))
    except Exception as exc:
        detail = str(exc) or "Unexpected speech model startup error."
        await runtime.mark_model_failed(detail)
        logger.exception(_json_log("whisper_load_failed", model=config.model_name, backend="faster_whisper", error=detail))
    finally:
        current_task = asyncio.current_task()
        async with runtime._startup_lock:
            if runtime._model_load_task is current_task:
                runtime._model_load_task = None

        logger.info(
            _json_log(
                "startup_complete",
                startup_state=runtime.startup_state,
                model=config.model_name,
                whisper_ready=_asr_backend.is_model_ready(config.model_name),
                compute_type=os.getenv("SPEECH_COMPUTE_TYPE", "int8"),
                device=os.getenv("SPEECH_DEVICE", "cpu"),
                failure_reason=runtime.model_failure_reason,
                failure_detail=runtime.model_failure_detail,
                max_concurrent_sessions=config.max_concurrent_sessions,
                max_queue_messages_per_conn=config.max_queue_messages_per_conn,
                max_audio_buffer_ms_per_conn=config.max_audio_buffer_ms_per_conn,
                transcribe_timeout_sec=config.transcribe_timeout_sec,
                vad_energy_threshold=config.vad_energy_threshold,
                vad_min_speech_ms=config.vad_min_speech_ms,
                vad_min_speech_ratio=config.vad_min_speech_ratio,
                vad_dynamic_threshold_multiplier=config.vad_dynamic_threshold_multiplier,
                stream_decode_interval_ms=config.stream_decode_interval_ms,
                stream_decode_overlap_ms=config.stream_decode_overlap_ms,
                stream_max_active_window_ms=config.stream_max_active_window_ms,
                stream_commit_agreement_passes=config.stream_commit_agreement_passes,
                speech_vad_backend=config.speech_vad_backend,
                speech_vad_fallback=config.speech_vad_fallback,
                strict_quality_mode=config.strict_quality_mode,
            )
        )


@app.on_event("startup")
async def on_startup() -> None:
    await _ensure_background_model_load()


@app.on_event("shutdown")
async def on_shutdown() -> None:
    task = await runtime.get_model_load_task()
    if task is not None and not task.done():
        task.cancel()
        await asyncio.gather(task, return_exceptions=True)


@app.get("/health")
async def health() -> dict[str, Any]:
    await _ensure_background_model_load()
    return {
        "status": "ok",
        "startupState": runtime.startup_state,
        "modelLoaded": runtime.model_loaded and _asr_backend.is_model_ready(config.model_name),
    }


@app.get("/health/ready")
async def health_ready() -> JSONResponse:
    await _ensure_background_model_load()
    active = await runtime.active_sessions()
    model_ready = runtime.model_loaded and _asr_backend.is_model_ready(config.model_name)
    model_loading = runtime.startup_state in {"starting", "model_loading"} and not model_ready
    can_accept = model_ready and active < config.max_concurrent_sessions
    body = {
        "status": "ok" if can_accept else ("not_ready" if not model_ready else "at_capacity"),
        "modelLoaded": model_ready,
        "failureReason": runtime.model_failure_reason or ("model_loading" if model_loading else None),
        "failureDetail": runtime.model_failure_detail,
        "startupState": runtime.startup_state,
        "activeSessions": active,
        "maxConcurrentSessions": config.max_concurrent_sessions,
        "uptimeSec": runtime.uptime_sec(),
    }
    return JSONResponse(status_code=200 if can_accept else 503, content=body)


@app.get("/health/diagnostics")
async def health_diagnostics() -> JSONResponse:
    await _ensure_background_model_load()
    active = await runtime.active_sessions()
    model_ready = runtime.model_loaded and _asr_backend.is_model_ready(config.model_name)
    model_loading = runtime.startup_state in {"starting", "model_loading"} and not model_ready
    snap = await runtime.metrics.snapshot()
    upload_metadata = await runtime.get_upload_metadata()

    transcribe_values = snap["transcribe"]
    avg_latency = 0.0
    p95_latency = 0.0
    if transcribe_values:
        avg_latency = round(sum(transcribe_values) / len(transcribe_values), 1)
        sorted_vals = sorted(transcribe_values)
        p95_idx = int(len(sorted_vals) * 0.95)
        p95_latency = round(sorted_vals[min(p95_idx, len(sorted_vals) - 1)], 1)

    body = {
        "model": config.model_name,
        "model_ready": model_ready,
        "model_loading": model_loading,
        "startup_state": runtime.startup_state,
        "startup_task_started_at": runtime.startup_task_started_at,
        "model_ready_at": runtime.model_ready_at,
        "compute_type": os.getenv("SPEECH_COMPUTE_TYPE", "int8"),
        "device": os.getenv("SPEECH_DEVICE", "cpu"),
        "failure_reason": runtime.model_failure_reason or ("model_loading" if model_loading else None),
        "failure_detail": runtime.model_failure_detail,
        "audio_input_contract": AUDIO_INPUT_CONTRACT,
        "live_input_sample_rate": SAMPLE_RATE,
        "live_input_channels": 1,
        "live_input_chunk_ms": LIVE_INPUT_CHUNK_MS,
        "vad_backend": _vad_classifier.active_backend,
        "silero_available": _vad_classifier.silero_available,
        "strict_quality_mode": config.strict_quality_mode,
        "active_sessions": active,
        "max_sessions": config.max_concurrent_sessions,
        "avg_transcribe_latency_ms": avg_latency,
        "p95_transcribe_latency_ms": p95_latency,
        "total_final_segments": snap["counters"].get("speech_final_messages_total", 0),
        "total_partial_segments": snap["counters"].get("speech_partial_messages_total", 0),
        "total_connections": snap["counters"].get("speech_ws_connections_total", 0),
        "total_errors": snap["counters"].get("speech_transcribe_errors_total", 0),
        "vad_voiced_chunks": snap["counters"].get("speech_vad_voiced_chunks_total", 0),
        "vad_rejected_chunks": snap["counters"].get("speech_vad_rejected_chunks_total", 0),
        "filtered_decode_results_total": snap["counters"].get("filtered_decode_results_total", 0),
        "empty_decode_results_total": snap["counters"].get("empty_decode_results_total", 0),
        "duplicate_finals_suppressed_total": snap["counters"].get("duplicate_finals_suppressed_total", 0),
        "last_upload_container": upload_metadata.container,
        "last_upload_codec": upload_metadata.codec,
        "last_upload_sample_rate": upload_metadata.sample_rate,
        "last_upload_channels": upload_metadata.channels,
        "last_upload_content_type": upload_metadata.content_type,
        "upload_normalization_applied": upload_metadata.normalization_applied,
        "uptime_sec": runtime.uptime_sec(),
    }
    return JSONResponse(status_code=200, content=body)


@app.post("/transcribe")
async def transcribe_upload(
    file: UploadFile = File(...),
    language: str = Query("tr", alias="language"),
    compute_stats: bool = Query(True),
) -> JSONResponse:
    await _ensure_background_model_load()
    normalized_language = (language or "tr").strip().lower()
    if normalized_language not in {"en", "tr"}:
        raise HTTPException(status_code=400, detail=f"Unsupported language '{language}'.")

    if not runtime.model_loaded or not _asr_backend.is_model_ready(config.model_name):
        raise HTTPException(status_code=503, detail="Speech model is not ready yet.")

    payload = await file.read()
    if not payload:
        raise HTTPException(status_code=400, detail="Uploaded audio file is empty.")

    upload_metadata = await _probe_upload_audio_metadata(
        payload,
        filename=file.filename,
        content_type=file.content_type,
    )
    logger.info(
        _json_log(
            "upload_received",
            audio_path="upload_file",
            filename=file.filename,
            content_type=file.content_type,
            container=upload_metadata.container,
            codec=upload_metadata.codec,
            sample_rate=upload_metadata.sample_rate,
            channels=upload_metadata.channels,
        )
    )

    try:
        pcm_audio = await _normalize_upload_to_pcm16(payload, filename=file.filename)
    except RuntimeError as exc:
        logger.warning(
            _json_log(
                "upload_normalize_failed",
                audio_path="upload_file",
                filename=file.filename,
                content_type=file.content_type,
                error=str(exc),
            )
        )
        raise HTTPException(status_code=400, detail=str(exc)) from exc

    duration_ms = _estimate_chunk_ms(len(pcm_audio))
    t0 = time.monotonic()
    try:
        result = await asyncio.wait_for(
            _asr_backend.transcribe(
                pcm_audio,
                model_name=config.model_name,
                language=normalized_language,
                task="transcribe",
                use_vad=False,
                start_ms=0,
                end_ms=duration_ms,
            ),
            timeout=config.transcribe_timeout_sec,
        )
    except asyncio.TimeoutError as exc:
        await runtime.metrics.inc("speech_transcribe_errors_total")
        await runtime.metrics.inc("speech_transcribe_timeouts_total")
        raise HTTPException(status_code=504, detail="Speech model timed out while processing uploaded audio.") from exc
    except TranscriptionError as exc:
        await runtime.metrics.inc("speech_transcribe_errors_total")
        await runtime.metrics.inc("speech_transcribe_model_errors_total")
        raise HTTPException(status_code=503, detail=exc.detail) from exc

    latency_ms = (time.monotonic() - t0) * 1000.0
    await runtime.metrics.observe_transcribe_latency(latency_ms)

    meta = result.get("meta") or {}
    if meta.get("filtered_segments", 0) > 0 or meta.get("window_suppressed", False):
        await runtime.metrics.inc("filtered_decode_results_total")
    if meta.get("empty_result", False):
        await runtime.metrics.inc("empty_decode_results_total")

    await runtime.record_upload_metadata(
        UploadAudioMetadata(
            container=upload_metadata.container,
            codec=upload_metadata.codec,
            sample_rate=upload_metadata.sample_rate,
            channels=upload_metadata.channels,
            content_type=file.content_type,
            filename=file.filename,
            normalization_applied=True,
        )
    )

    segments = result.get("segments", [])
    full_text = " ".join(str(segment.get("text", "")).strip() for segment in segments).strip()
    word_count = len(full_text.split()) if full_text else 0
    stats = result.get("stats") or {}

    logger.info(
        _json_log(
            "upload_transcribe_complete",
            audio_path="upload_file",
            model=config.model_name,
            language=normalized_language,
            filename=file.filename,
            duration_ms=duration_ms,
            transcribe_latency_ms=round(latency_ms, 2),
            segment_count=len(segments),
            filtered_segments=meta.get("filtered_segments", 0),
            empty_result=bool(meta.get("empty_result", False)),
        )
    )

    body: dict[str, Any] = {
        "segments": segments,
        "full_text": full_text,
        "duration_ms": duration_ms,
        "word_count": word_count,
        "wpm": stats.get("wpm", 0),
        "filler_count": stats.get("filler_count", 0),
        "pause_count": stats.get("pause_count", 0),
        "pause_ms": stats.get("pause_ms", 0),
        "audio_format": {
            "input_contract": AUDIO_INPUT_CONTRACT,
            "upload_container": upload_metadata.container,
            "upload_codec": upload_metadata.codec,
            "upload_sample_rate": upload_metadata.sample_rate,
            "upload_channels": upload_metadata.channels,
            "normalization_applied": True,
        },
    }
    if compute_stats:
        body["stats"] = stats

    return JSONResponse(status_code=200, content=body)


@app.get("/metrics")
async def metrics() -> PlainTextResponse:
    snap = await runtime.metrics.snapshot()
    lines: list[str] = []

    def add_counter(name: str, value: int) -> None:
        lines.append(f"# TYPE {name} counter")
        lines.append(f"{name} {value}")

    for key, value in snap["counters"].items():
        add_counter(key, value)

    lines.append("# TYPE speech_active_sessions gauge")
    lines.append(f"speech_active_sessions {snap['active_sessions']}")
    lines.append("# TYPE speech_queue_backlog_current gauge")
    lines.append(f"speech_queue_backlog_current {snap['queue_backlog_current']}")

    _append_summary(lines, "speech_time_to_first_partial_ms", snap["partial"])
    _append_summary(lines, "speech_time_to_first_final_ms", snap["final"])
    _append_summary(lines, "speech_transcribe_latency_ms", snap["transcribe"])

    payload = "\n".join(lines) + "\n"
    return PlainTextResponse(content=payload, media_type="text/plain; version=0.0.4")


def _append_summary(lines: list[str], name: str, values: list[float]) -> None:
    lines.append(f"# TYPE {name} summary")
    if not values:
        lines.append(f"{name}_count 0")
        lines.append(f"{name}_sum 0")
        lines.append(f"{name}_p50 0")
        lines.append(f"{name}_p95 0")
        return

    sorted_values = sorted(values)
    count = len(sorted_values)
    total = sum(sorted_values)
    p50 = _percentile(sorted_values, 0.50)
    p95 = _percentile(sorted_values, 0.95)
    lines.append(f"{name}_count {count}")
    lines.append(f"{name}_sum {total:.3f}")
    lines.append(f"{name}_p50 {p50:.3f}")
    lines.append(f"{name}_p95 {p95:.3f}")


def _percentile(sorted_values: list[float], q: float) -> float:
    if not sorted_values:
        return 0.0
    if len(sorted_values) == 1:
        return float(sorted_values[0])
    idx = (len(sorted_values) - 1) * q
    lo = math.floor(idx)
    hi = math.ceil(idx)
    if lo == hi:
        return float(sorted_values[lo])
    frac = idx - lo
    return float(sorted_values[lo] + (sorted_values[hi] - sorted_values[lo]) * frac)


@app.websocket("/ws/transcribe")
async def ws_transcribe(websocket: WebSocket) -> None:
    await _ensure_background_model_load()
    await websocket.accept()

    session_id = websocket.query_params.get("session_id", "").strip()
    default_language = (websocket.query_params.get("lang", "en") or "en").lower()
    if default_language not in {"en", "tr"}:
        default_language = "en"
    connection_id = uuid.uuid4().hex[:12]

    if not runtime.model_loaded or not _asr_backend.is_model_ready(config.model_name):
        await runtime.metrics.inc("speech_ws_rejections_total")
        logger.warning(
            _json_log(
                "connection_rejected",
                reason="model_not_ready",
                session_id=session_id,
                connection_id=connection_id,
                default_language=default_language,
            )
        )
        await _send_ws_error(
            websocket,
            error_code="model_not_ready",
            detail="Live transcript model is not ready yet.",
            retryable=True,
        )
        await websocket.close(code=1013, reason="speech model not ready")
        return

    if not await runtime.try_acquire_session(connection_id):
        await runtime.metrics.inc("speech_ws_rejections_total")
        logger.warning(
            _json_log(
                "connection_rejected",
                reason="max_concurrent_sessions_reached",
                session_id=session_id,
                connection_id=connection_id,
                default_language=default_language,
            )
        )
        await _send_ws_error(
            websocket,
            error_code="max_concurrent_sessions_reached",
            detail="Server is at max concurrent session capacity.",
            retryable=True,
        )
        await websocket.close(code=1013, reason="max concurrent sessions reached")
        return

    session = TranscriptionSession(
        session_id=session_id,
        connection_id=connection_id,
        default_language=default_language,
        default_model=config.model_name,
        inbound_queue=asyncio.Queue(maxsize=config.max_queue_messages_per_conn),
        transcribe_queue=asyncio.Queue(),
    )

    await runtime.metrics.inc("speech_ws_connections_total")
    logger.info(
        _json_log(
            "connection_accepted",
            session_id=session.session_id,
            connection_id=session.connection_id,
            default_language=session.default_language,
        )
    )

    receiver = asyncio.create_task(_receiver_loop(websocket=websocket, session=session))
    processor = asyncio.create_task(_processor_loop(session=session))
    transcriber = asyncio.create_task(_transcriber_loop(websocket=websocket, session=session))

    try:
        done, _pending = await asyncio.wait({receiver, processor, transcriber}, return_when=asyncio.FIRST_EXCEPTION)
        for task in done:
            exc = task.exception()
            if exc is not None:
                logger.exception(
                    _json_log(
                        "connection_task_error",
                        session_id=session.session_id,
                        connection_id=session.connection_id,
                        language=session.language,
                        error=str(exc),
                    )
                )
    finally:
        session.stop_event.set()
        for task in (receiver, processor, transcriber):
            if not task.done():
                task.cancel()
        await asyncio.gather(receiver, processor, transcriber, return_exceptions=True)

        try:
            if websocket.application_state != WebSocketState.DISCONNECTED:
                await websocket.close()
        except Exception:
            pass

        await runtime.metrics.inc("speech_ws_disconnects_total")
        await runtime.release_session(session.connection_id)
        logger.info(
            _json_log(
                "connection_closed",
                session_id=session.session_id,
                connection_id=session.connection_id,
                language=session.language,
            )
        )


async def _receiver_loop(websocket: WebSocket, session: TranscriptionSession) -> None:
    while not session.stop_event.is_set():
        try:
            raw = await asyncio.wait_for(websocket.receive_text(), timeout=config.client_idle_timeout_sec)
        except asyncio.TimeoutError:
            logger.warning(
                _json_log(
                    "idle_timeout",
                    session_id=session.session_id,
                    connection_id=session.connection_id,
                    language=session.language,
                    timeout_sec=config.client_idle_timeout_sec,
                )
            )
            try:
                await _send_ws_error(
                    websocket,
                    error_code="idle_timeout",
                    detail=f"No client message received for {config.client_idle_timeout_sec}s",
                    retryable=False,
                )
            except Exception:
                pass
            session.stop_event.set()
            return
        except WebSocketDisconnect:
            session.stop_event.set()
            return
        except Exception as ex:
            logger.warning(
                _json_log(
                    "receive_error",
                    session_id=session.session_id,
                    connection_id=session.connection_id,
                    language=session.language,
                    error=str(ex),
                )
            )
            session.stop_event.set()
            return

        try:
            msg = parse_client_message(raw, default_model=config.model_name, default_language=session.default_language)
        except ProtocolError as ex:
            logger.warning(
                _json_log(
                    "protocol_error",
                    session_id=session.session_id,
                    connection_id=session.connection_id,
                    language=session.language,
                    error_code=ex.error_code,
                    detail=ex.detail,
                )
            )
            await _send_ws_error(websocket, error_code=ex.error_code, detail=ex.detail, retryable=False)
            session.stop_event.set()
            return

        if isinstance(msg, ClientConfigMessage):
            if session.is_configured:
                await _send_ws_error(
                    websocket,
                    error_code="duplicate_config",
                    detail="Session configuration was already received for this websocket.",
                    retryable=False,
                )
                session.stop_event.set()
                return

            try:
                await _asr_backend.load_model(msg.config.model)
            except TranscriptionError as ex:
                await _send_ws_error(
                    websocket,
                    error_code=ex.error_code,
                    detail=ex.detail,
                    retryable=False,
                )
                session.stop_event.set()
                return

            session.apply_config(msg.config)
            logger.info(
                _json_log(
                    "session_configured",
                    session_id=session.session_id,
                    connection_id=session.connection_id,
                    language=session.language,
                    model=session.model,
                    task=session.task,
                    use_vad=session.use_vad,
                    backend="faster_whisper",
                    vad_backend=config.speech_vad_backend,
                    vad_fallback=config.speech_vad_fallback,
                    vad_active_backend=_vad_classifier.active_backend,
                )
            )
            await websocket.send_json(
                {
                    "type": "ready",
                    "session": {
                        "language": session.language,
                        "model": session.model,
                        "task": session.task,
                        "use_vad": session.use_vad,
                    },
                }
            )
            continue

        if not session.is_configured:
            await _send_ws_error(
                websocket,
                error_code="config_required",
                detail="Send a config message before streaming audio.",
                retryable=False,
            )
            session.stop_event.set()
            return

        if isinstance(msg, ClientAudioMessage):
            try:
                audio_bytes = base64.b64decode(msg.data_b64, validate=True)
            except (binascii.Error, ValueError):
                await runtime.metrics.inc("speech_audio_decode_errors_total")
                logger.warning(
                    _json_log(
                        "audio_decode_error",
                        session_id=session.session_id,
                        connection_id=session.connection_id,
                        language=session.language,
                        seq=msg.seq,
                    )
                )
                continue

            chunk_ms = _estimate_chunk_ms(len(audio_bytes))
            await runtime.metrics.inc("speech_audio_chunks_received_total")
            accepted = await _put_inbound_chunk(
                websocket=websocket,
                session=session,
                chunk=InboundChunk(kind="audio", seq=msg.seq, audio_bytes=audio_bytes, chunk_ms=chunk_ms),
            )
            if not accepted:
                return

            await runtime.update_backlog(session.connection_id, session.inbound_queue.qsize())
            logger.info(
                _json_log(
                    "chunk_received",
                    session_id=session.session_id,
                    connection_id=session.connection_id,
                    language=session.language,
                    seq=msg.seq,
                    queue_size=session.inbound_queue.qsize(),
                    chunk_ms=chunk_ms,
                )
            )
            continue

        if isinstance(msg, ClientEndMessage):
            accepted = await _put_inbound_chunk(
                websocket=websocket,
                session=session,
                chunk=InboundChunk(kind="end"),
            )
            if not accepted:
                return

            await runtime.update_backlog(session.connection_id, session.inbound_queue.qsize())
            logger.info(
                _json_log(
                    "end_received",
                    session_id=session.session_id,
                    connection_id=session.connection_id,
                    language=session.language,
                    queue_size=session.inbound_queue.qsize(),
                )
            )
            return


async def _put_inbound_chunk(
    websocket: WebSocket,
    session: TranscriptionSession,
    chunk: InboundChunk,
) -> bool:
    try:
        await asyncio.wait_for(session.inbound_queue.put(chunk), timeout=config.backpressure_queue_wait_ms / 1000.0)
        return True
    except asyncio.TimeoutError:
        await runtime.metrics.inc("speech_backpressure_events_total")
        if chunk.kind == "audio":
            await runtime.metrics.inc("speech_audio_chunks_dropped_total")
        logger.warning(
            _json_log(
                "queue_backpressure_timeout",
                session_id=session.session_id,
                connection_id=session.connection_id,
                language=session.language,
                seq=chunk.seq,
                queue_size=session.inbound_queue.qsize(),
                max_queue=config.max_queue_messages_per_conn,
                wait_ms=config.backpressure_queue_wait_ms,
            )
        )
        await _send_ws_error(
            websocket,
            error_code="backpressure_overloaded",
            detail="Live transcript is overloaded. Please pause briefly and retry.",
            retryable=True,
        )
        session.stop_event.set()
        return False


async def _processor_loop(session: TranscriptionSession) -> None:
    total_audio_ms = 0
    noise_floor_rms = max(0.0005, config.vad_energy_threshold * 0.5)
    stream = session.stream_state
    commit = session.commit_state
    silero_window_ms = max(1000, config.stream_decode_interval_ms)

    async def enqueue_decode(reason: str, *, force_finalize: bool) -> None:
        if stream.duration_ms <= 0 or not stream.active_audio:
            return

        if not force_finalize and not _has_meaningful_speech(stream.duration_ms, stream.voiced_ms):
            return

        await session.transcribe_queue.put(
            TranscribeJob(
                audio_bytes=bytes(stream.active_audio),
                start_ms=stream.start_ms,
                end_ms=stream.start_ms + stream.duration_ms,
                reason=reason,
                force_finalize=force_finalize,
            )
        )
        stream.pending_voiced_ms = 0
        session.decode_jobs_queued += 1
        session.suspected_speech_ms_without_decode = 0
        session.stall_warning_emitted = False
        if session.first_decode_job_at_ms == 0:
            session.first_decode_job_at_ms = stream.start_ms + stream.duration_ms
            logger.info(
                _json_log(
                    "first_decode_job_queued",
                    session_id=session.session_id,
                    connection_id=session.connection_id,
                    language=session.language,
                    reason=reason,
                    t_ms=session.first_decode_job_at_ms,
                )
            )
        await runtime.metrics.inc("speech_decode_jobs_queued_total")
        logger.info(
            _json_log(
                "decode_job_queued",
                session_id=session.session_id,
                connection_id=session.connection_id,
                language=session.language,
                reason=reason,
                force_finalize=force_finalize,
                start_ms=stream.start_ms,
                end_ms=stream.start_ms + stream.duration_ms,
                duration_ms=stream.duration_ms,
                voiced_ms=stream.voiced_ms,
                trailing_silence_ms=stream.trailing_silence_ms,
            )
        )

    def reset_stream_window() -> None:
        stream.reset()

    def append_chunk(audio_bytes: bytes, chunk_ms: int, *, is_voiced: bool) -> None:
        if not stream.speech_started:
            stream.speech_started = True
            stream.start_ms = max(0, total_audio_ms - chunk_ms)

        stream.active_audio.extend(audio_bytes)
        stream.duration_ms += chunk_ms
        if is_voiced:
            stream.voiced_ms += chunk_ms
            stream.pending_voiced_ms += chunk_ms
        _trim_stream_window(stream)

    try:
        while True:
            if session.stop_event.is_set() and session.inbound_queue.empty():
                break

            try:
                msg = await asyncio.wait_for(session.inbound_queue.get(), timeout=0.5)
            except asyncio.TimeoutError:
                continue

            await runtime.update_backlog(session.connection_id, session.inbound_queue.qsize())

            if msg.kind == "end":
                await enqueue_decode("client_end", force_finalize=True)
                reset_stream_window()
                session.stop_event.set()
                break

            if msg.kind != "audio" or msg.audio_bytes is None:
                continue

            session.received_audio_chunks += 1
            logger.info(
                _json_log(
                    "chunk_processing",
                    session_id=session.session_id,
                    connection_id=session.connection_id,
                    language=session.language,
                    seq=msg.seq,
                    queue_size=session.inbound_queue.qsize(),
                    chunk_ms=msg.chunk_ms,
                )
            )

            chunk_rms = _pcm16_rms(msg.audio_bytes)
            speech_threshold = _resolve_speech_threshold(
                base_threshold=config.vad_energy_threshold,
                noise_floor_rms=noise_floor_rms,
                multiplier=config.vad_dynamic_threshold_multiplier,
            )
            total_audio_ms += msg.chunk_ms
            if session.use_vad:
                session.vad_history_ms = _append_recent_audio_window(
                    session.vad_history_audio,
                    session.vad_history_ms,
                    msg.audio_bytes,
                    msg.chunk_ms,
                    max_window_ms=silero_window_ms,
                )
                vad_decision = _vad_classifier.classify_chunk(
                    msg.audio_bytes,
                    chunk_ms=msg.chunk_ms,
                    speech_threshold=speech_threshold,
                    chunk_rms=chunk_rms,
                    rolling_audio_bytes=bytes(session.vad_history_audio),
                    rolling_chunk_ms=session.vad_history_ms,
                )
            else:
                vad_decision = VadDecision(
                    is_speech=True,
                    speech_ms=msg.chunk_ms,
                    backend="disabled",
                    energy_is_speech=True,
                    energy_speech_ms=msg.chunk_ms,
                    chunk_rms=chunk_rms,
                    speech_threshold=speech_threshold,
                )
            is_voiced = vad_decision.is_speech
            commit.recent_voiced_ms = vad_decision.speech_ms if is_voiced else 0
            commit.recent_unvoiced_ms = 0 if is_voiced else msg.chunk_ms
            stream.recent_voiced_ms = commit.recent_voiced_ms
            stream.recent_unvoiced_ms = commit.recent_unvoiced_ms
            await runtime.metrics.inc("speech_vad_voiced_chunks_total" if is_voiced else "speech_vad_rejected_chunks_total")

            if logger.isEnabledFor(logging.DEBUG):
                logger.debug(
                    _json_log(
                        "vad_chunk_decision",
                        session_id=session.session_id,
                        connection_id=session.connection_id,
                        language=session.language,
                        seq=msg.seq,
                        chunk_ms=msg.chunk_ms,
                        chunk_rms=round(chunk_rms, 6),
                        speech_threshold=round(speech_threshold, 6),
                        vad_backend=vad_decision.backend,
                        final_is_voiced=is_voiced,
                        energy_voiced=vad_decision.energy_is_speech,
                        silero_voiced=vad_decision.silero_is_speech,
                        silero_speech_ms=vad_decision.silero_speech_ms,
                        silero_window_ms=vad_decision.silero_window_ms,
                    )
                )

            if session.decode_jobs_queued == 0:
                likely_speech_ms = msg.chunk_ms if (vad_decision.energy_is_speech or is_voiced) else 0
                session.suspected_speech_ms_without_decode += likely_speech_ms
                stall_threshold_ms = max(
                    config.stream_decode_interval_ms + max(500, msg.chunk_ms * 2),
                    config.vad_min_speech_ms + config.vad_silence_ms,
                )
                if session.suspected_speech_ms_without_decode >= stall_threshold_ms and not session.stall_warning_emitted:
                    session.stall_warning_emitted = True
                    await runtime.metrics.inc("speech_streams_stalled_total")
                    logger.warning(
                        _json_log(
                            "stream_decode_stalled",
                            session_id=session.session_id,
                            connection_id=session.connection_id,
                            language=session.language,
                            suspected_speech_ms=session.suspected_speech_ms_without_decode,
                            received_audio_chunks=session.received_audio_chunks,
                            chunk_rms=round(chunk_rms, 6),
                            speech_threshold=round(speech_threshold, 6),
                            vad_backend=vad_decision.backend,
                            energy_voiced=vad_decision.energy_is_speech,
                            silero_voiced=vad_decision.silero_is_speech,
                        )
                    )

            if stream.speech_started and stream.duration_ms + msg.chunk_ms > config.max_audio_buffer_ms_per_conn:
                logger.warning(
                    _json_log(
                        "audio_buffer_limit_exceeded",
                        session_id=session.session_id,
                        connection_id=session.connection_id,
                        language=session.language,
                        buffer_ms=stream.duration_ms,
                        incoming_chunk_ms=msg.chunk_ms,
                        max_buffer_ms=config.max_audio_buffer_ms_per_conn,
                    )
                )
                await enqueue_decode("buffer_limit", force_finalize=True)
                reset_stream_window()

            if not stream.speech_started and not is_voiced:
                if session.use_vad:
                    noise_floor_rms = _update_noise_floor(noise_floor_rms, chunk_rms)
                continue

            append_chunk(msg.audio_bytes, msg.chunk_ms, is_voiced=is_voiced)

            if session.use_vad and not is_voiced:
                stream.trailing_silence_ms += msg.chunk_ms
                noise_floor_rms = _update_noise_floor(noise_floor_rms, chunk_rms)
            else:
                stream.trailing_silence_ms = 0

            if stream.pending_voiced_ms >= config.stream_decode_interval_ms:
                await enqueue_decode("stream_interval", force_finalize=False)

            if session.use_vad and stream.trailing_silence_ms >= config.vad_silence_ms and stream.duration_ms > 0:
                logger.info(
                    _json_log(
                        "vad_finalize",
                        session_id=session.session_id,
                        connection_id=session.connection_id,
                        language=session.language,
                        silence_ms=stream.trailing_silence_ms,
                        buffer_ms=stream.duration_ms,
                        speech_threshold=round(speech_threshold, 6),
                        noise_floor_rms=round(noise_floor_rms, 6),
                        voiced_ms=stream.voiced_ms,
                        vad_backend=vad_decision.backend,
                    )
                )
                await enqueue_decode("vad_silence", force_finalize=True)
                reset_stream_window()
    finally:
        await session.transcribe_queue.put(None)


async def _transcriber_loop(websocket: WebSocket, session: TranscriptionSession) -> None:
    first_partial_sent = False
    first_final_sent = False

    while True:
        if session.stop_event.is_set() and session.transcribe_queue.empty():
            break

        try:
            job = await asyncio.wait_for(session.transcribe_queue.get(), timeout=0.5)
        except asyncio.TimeoutError:
            continue

        if job is None:
            break

        indicator_text = _build_processing_indicator(job.end_ms - job.start_ms)
        await websocket.send_json({"type": "partial_status", "text": indicator_text, "t_ms": job.end_ms})
        await runtime.metrics.inc("speech_partial_status_messages_total")

        t0 = time.monotonic()
        try:
            result = await asyncio.wait_for(
                _transcribe_utterance(job.audio_bytes, session, job.start_ms, job.end_ms),
                timeout=config.transcribe_timeout_sec,
            )
            latency_ms = (time.monotonic() - t0) * 1000.0
            await runtime.metrics.observe_transcribe_latency(latency_ms)
            logger.info(
                _json_log(
                    "transcribe_complete",
                    session_id=session.session_id,
                    connection_id=session.connection_id,
                    language=session.language,
                    reason=job.reason,
                    duration_ms=max(1, job.end_ms - job.start_ms),
                    transcribe_latency_ms=round(latency_ms, 2),
                    task=session.task,
                )
            )
        except asyncio.TimeoutError:
            await runtime.metrics.inc("speech_transcribe_errors_total")
            await runtime.metrics.inc("speech_transcribe_timeouts_total")
            logger.warning(
                _json_log(
                    "transcribe_timeout",
                    session_id=session.session_id,
                    connection_id=session.connection_id,
                    language=session.language,
                    timeout_sec=config.transcribe_timeout_sec,
                    reason=job.reason,
                )
            )
            await _send_ws_error(
                websocket,
                error_code="transcribe_timeout",
                detail="Speech model timed out while processing an utterance.",
                retryable=True,
            )
            continue
        except TranscriptionError as ex:
            await runtime.metrics.inc("speech_transcribe_errors_total")
            await runtime.metrics.inc("speech_transcribe_model_errors_total")
            logger.warning(
                _json_log(
                    "transcribe_error",
                    session_id=session.session_id,
                    connection_id=session.connection_id,
                    language=session.language,
                    reason=job.reason,
                    error=ex.detail,
                    error_code=ex.error_code,
                )
            )
            await _send_ws_error(
                websocket,
                error_code=ex.error_code,
                detail=ex.detail,
                retryable=ex.error_code != "model_unavailable",
            )
            if ex.error_code == "model_unavailable":
                session.stop_event.set()
                break
            continue

        meta = result.get("meta") or {}
        if meta.get("filtered_segments", 0) > 0 or meta.get("window_suppressed", False):
            await runtime.metrics.inc("filtered_decode_results_total")
        if meta.get("empty_result", False):
            await runtime.metrics.inc("empty_decode_results_total")

        update = _apply_streaming_decode(session, result["segments"], force_finalize=job.force_finalize)
        final_text = update.final_text
        partial_text = update.partial_text
        carry_over_text = update.carry_over_text

        if update.final_segments:
            final_signature = _build_segment_signature(update.final_segments)
            if final_signature and final_signature == session.last_final_signature:
                await runtime.metrics.inc("duplicate_finals_suppressed_total")
                logger.info(
                    _json_log(
                        "duplicate_final_suppressed",
                        session_id=session.session_id,
                        connection_id=session.connection_id,
                        language=session.language,
                        reason=job.reason,
                        final_text_length=len(final_text),
                    )
                )
            else:
                await websocket.send_json(
                    {
                        "type": "final",
                        "segments": [segment.to_payload() for segment in update.final_segments],
                        "stats": result["stats"],
                    }
                )
                await runtime.metrics.inc("speech_final_messages_total")
                if not job.force_finalize:
                    await runtime.metrics.inc("speech_commit_prefix_promotions_total")
                if update.carry_over_segments and job.force_finalize:
                    await runtime.metrics.inc("speech_finalize_carry_over_total")
                session.total_final_text_length += len(final_text)
                session.last_partial_emitted_at_ms = 0
                session.last_final_signature = final_signature
                if not first_final_sent:
                    first_final_sent = True
                    elapsed_ms = (time.monotonic() - session.conn_started) * 1000.0
                    await runtime.metrics.observe_final_latency(elapsed_ms)
                    logger.info(
                        _json_log(
                            "first_final_emitted",
                            session_id=session.session_id,
                            connection_id=session.connection_id,
                            language=session.language,
                            elapsed_ms=round(elapsed_ms, 2),
                        )
                    )

        partial_t_ms = update.partial_segments[-1].end_ms if update.partial_segments else job.end_ms
        if partial_text:
            if partial_text != session.last_partial_text or partial_t_ms > session.last_partial_t_ms:
                await websocket.send_json({"type": "partial", "text": partial_text, "t_ms": partial_t_ms})
                await runtime.metrics.inc("speech_partial_messages_total")
                session.partials_seen = True
                session.max_partial_text_length = max(session.max_partial_text_length, len(partial_text))
                session.last_partial_text = partial_text
                session.last_partial_t_ms = partial_t_ms
                session.last_partial_emitted_at_ms = partial_t_ms
                if not first_partial_sent:
                    first_partial_sent = True
                    elapsed_ms = (time.monotonic() - session.conn_started) * 1000.0
                    await runtime.metrics.observe_partial_latency(elapsed_ms)
                    logger.info(
                        _json_log(
                            "first_partial_emitted",
                            session_id=session.session_id,
                            connection_id=session.connection_id,
                            language=session.language,
                            elapsed_ms=round(elapsed_ms, 2),
                        )
                    )
        elif session.last_partial_text:
            await websocket.send_json({"type": "partial", "text": "", "t_ms": partial_t_ms})
            session.last_partial_text = ""
            session.last_partial_t_ms = partial_t_ms
            session.last_partial_emitted_at_ms = 0

        if job.force_finalize and update.carry_over_segments:
            logger.info(
                _json_log(
                    "finalize_carry_over_applied",
                    session_id=session.session_id,
                    connection_id=session.connection_id,
                    language=session.language,
                    carry_over_length=len(carry_over_text),
                    carry_over_segments=len(update.carry_over_segments),
                    reason=job.reason,
                )
            )

        projected_final_text_length = session.total_final_text_length
        if job.force_finalize and session.partials_seen and projected_final_text_length + 10 < session.max_partial_text_length:
            await runtime.metrics.inc("speech_finalize_empty_after_partials_total")
            logger.warning(
                _json_log(
                    "finalize_short_after_partials",
                    session_id=session.session_id,
                    connection_id=session.connection_id,
                    language=session.language,
                    reason=job.reason,
                    total_final_text_length=projected_final_text_length,
                    max_partial_text_length=session.max_partial_text_length,
                    carry_over_length=len(carry_over_text),
                )
            )

        logger.info(
            _json_log(
                "decode_result_applied",
                session_id=session.session_id,
                connection_id=session.connection_id,
                language=session.language,
                reason=job.reason,
                force_finalize=job.force_finalize,
                start_ms=job.start_ms,
                end_ms=job.end_ms,
                hypothesis_segments=len(result["segments"]),
                committed_segments=len(update.final_segments),
                partial_segments=len(update.partial_segments),
                partial_text_length=len(partial_text),
                final_text_length=len(final_text),
                carry_over_length=len(carry_over_text),
                filtered_segments=meta.get("filtered_segments", 0),
                empty_result=bool(meta.get("empty_result", False)),
                window_suppressed=bool(meta.get("window_suppressed", False)),
                agreement_mode=update.agreement_mode,
            )
        )


def _estimate_chunk_ms(byte_count: int) -> int:
    if byte_count <= 0:
        return 0
    return max(1, int(round((byte_count / BYTES_PER_SECOND) * 1000.0)))


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


def _resolve_speech_threshold(base_threshold: float, noise_floor_rms: float, multiplier: float) -> float:
    return max(base_threshold, noise_floor_rms * multiplier)


def _update_noise_floor(current_noise_floor: float, observed_rms: float) -> float:
    smoothed = (current_noise_floor * 0.92) + (observed_rms * 0.08)
    return max(0.0005, smoothed)


def _has_meaningful_speech(window_ms: int, voiced_ms: int) -> bool:
    if window_ms <= 0:
        return False
    voiced_ratio = voiced_ms / window_ms
    return voiced_ms >= min(window_ms, config.vad_min_speech_ms) and voiced_ratio >= config.vad_min_speech_ratio


def _append_recent_audio_window(
    buffer: bytearray,
    current_duration_ms: int,
    audio_bytes: bytes,
    chunk_ms: int,
    *,
    max_window_ms: int,
) -> int:
    buffer.extend(audio_bytes)
    current_duration_ms += chunk_ms
    if current_duration_ms <= max_window_ms:
        return current_duration_ms

    trim_ms = current_duration_ms - max_window_ms
    trim_bytes = _pcm_ms_to_byte_count(trim_ms)
    if trim_bytes > 0:
        del buffer[:trim_bytes]
    return max(0, current_duration_ms - trim_ms)


def _trim_stream_window(stream_state: Any) -> None:
    if stream_state.duration_ms <= config.stream_max_active_window_ms:
        return

    max_trim_ms = max(0, stream_state.duration_ms - config.stream_decode_overlap_ms)
    trim_ms = min(stream_state.duration_ms - config.stream_max_active_window_ms, max_trim_ms)
    if trim_ms <= 0:
        return

    trim_bytes = _pcm_ms_to_byte_count(trim_ms)
    del stream_state.active_audio[:trim_bytes]
    stream_state.duration_ms = max(0, stream_state.duration_ms - trim_ms)
    stream_state.voiced_ms = max(0, stream_state.voiced_ms - min(stream_state.voiced_ms, trim_ms))
    stream_state.start_ms += trim_ms


def _pcm_ms_to_byte_count(duration_ms: int) -> int:
    raw = int(round((duration_ms / 1000.0) * BYTES_PER_SECOND))
    if raw % 2 == 1:
        raw += 1
    return max(0, raw)


def _guess_file_suffix(filename: str | None, content_type: str | None) -> str:
    suffix = Path((filename or "").strip()).suffix.lower()
    if suffix:
        return suffix

    normalized_type = (content_type or "").strip().lower()
    return {
        "audio/webm": ".webm",
        "video/webm": ".webm",
        "audio/ogg": ".ogg",
        "audio/wav": ".wav",
        "audio/x-wav": ".wav",
        "audio/mpeg": ".mp3",
        "audio/mp4": ".m4a",
        "video/mp4": ".mp4",
    }.get(normalized_type, ".bin")


def _clean_optional_string(value: Any) -> str | None:
    if value is None:
        return None
    cleaned = str(value).strip()
    return cleaned or None


def _safe_optional_int(value: Any) -> int | None:
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def _build_segment_signature(segments: list[Any]) -> str:
    parts: list[str] = []
    for segment in segments:
        start_ms = int(getattr(segment, "start_ms", 0))
        end_ms = int(getattr(segment, "end_ms", 0))
        text = " ".join(str(getattr(segment, "text", "")).strip().lower().split())
        if not text:
            continue
        parts.append(f"{start_ms}:{end_ms}:{text}")
    return "|".join(parts)


async def _transcribe_utterance(
    audio_bytes: bytes,
    session: TranscriptionSession,
    start_ms: int,
    end_ms: int,
) -> dict[str, Any]:
    return await _asr_backend.transcribe(
        audio_bytes,
        model_name=session.model,
        language=session.language,
        task=session.task,
        use_vad=session.use_vad,
        start_ms=start_ms,
        end_ms=end_ms,
    )


async def _probe_upload_audio_metadata(
    payload: bytes,
    *,
    filename: str | None,
    content_type: str | None,
) -> UploadAudioMetadata:
    suffix = _guess_file_suffix(filename, content_type)
    with tempfile.TemporaryDirectory(prefix="speech-upload-") as temp_dir:
        input_path = Path(temp_dir) / f"input{suffix}"
        input_path.write_bytes(payload)

        try:
            proc = await asyncio.create_subprocess_exec(
                "ffprobe",
                "-v",
                "error",
                "-print_format",
                "json",
                "-show_streams",
                "-show_format",
                str(input_path),
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
            )
        except OSError:
            return UploadAudioMetadata(content_type=content_type, filename=filename)
        stdout, _stderr = await proc.communicate()
        if proc.returncode != 0 or not stdout:
            return UploadAudioMetadata(content_type=content_type, filename=filename)

    try:
        probe = json.loads(stdout.decode("utf-8"))
    except json.JSONDecodeError:
        return UploadAudioMetadata(content_type=content_type, filename=filename)

    audio_stream = next((stream for stream in probe.get("streams", []) if stream.get("codec_type") == "audio"), {})
    format_info = probe.get("format") or {}
    return UploadAudioMetadata(
        container=_clean_optional_string(format_info.get("format_name")),
        codec=_clean_optional_string(audio_stream.get("codec_name")),
        sample_rate=_safe_optional_int(audio_stream.get("sample_rate")),
        channels=_safe_optional_int(audio_stream.get("channels")),
        content_type=content_type,
        filename=filename,
    )


async def _normalize_upload_to_pcm16(payload: bytes, *, filename: str | None) -> bytes:
    suffix = _guess_file_suffix(filename, None)
    with tempfile.TemporaryDirectory(prefix="speech-upload-") as temp_dir:
        input_path = Path(temp_dir) / f"input{suffix}"
        input_path.write_bytes(payload)

        try:
            proc = await asyncio.create_subprocess_exec(
                "ffmpeg",
                "-nostdin",
                "-v",
                "error",
                "-i",
                str(input_path),
                "-f",
                "s16le",
                "-acodec",
                "pcm_s16le",
                "-ac",
                "1",
                "-ar",
                str(SAMPLE_RATE),
                "pipe:1",
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
            )
        except OSError as exc:
            raise RuntimeError("ffmpeg is not available to normalize uploaded audio.") from exc
        stdout, stderr = await proc.communicate()

    if proc.returncode != 0 or not stdout:
        detail = stderr.decode("utf-8", errors="ignore").strip() or "ffmpeg failed to decode the uploaded audio."
        raise RuntimeError(detail)

    return stdout


def _apply_streaming_decode(
    session: TranscriptionSession,
    segments: list[dict[str, Any]],
    *,
    force_finalize: bool,
):
    hypothesis = build_hypothesis_segments(
        segments,
        last_committed_end_ms=session.commit_state.last_committed_end_ms,
        committed_segments=session.commit_state.committed_segments,
    )
    return apply_decode_result(
        session.commit_state,
        hypothesis,
        force_finalize=force_finalize,
        agreement_passes=config.stream_commit_agreement_passes,
    )


def _build_processing_indicator(buffer_ms: int) -> str:
    seconds = max(0.5, buffer_ms / 1000.0)
    return f"Processing {seconds:.1f}s of recent audio..."


async def _send_ws_error(
    websocket: WebSocket,
    error_code: str,
    detail: str,
    retryable: bool,
) -> None:
    if websocket.application_state == WebSocketState.DISCONNECTED:
        return
    await websocket.send_json(
        {
            "type": "error",
            "error": error_code,
            "detail": detail,
            "retryable": retryable,
        }
    )
