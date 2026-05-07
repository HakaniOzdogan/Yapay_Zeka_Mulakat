"""Lean Speech Service — sıfırdan yeniden yazılmış, düşük gecikmeli canlı transkripsiyon.

Eski 1817-satırlık main.py yerine ~500 satırlık temiz mimari:
- 2 async loop (receiver + unified processor/transcriber)
- Direkt WebSocket iletişimi, ara queue yok
- Modüler AudioProcessor'a delege eder
"""

from __future__ import annotations

import asyncio
import base64
import binascii
import io
import json
import logging
import math
import os
import struct
import tempfile
import time
import uuid
from pathlib import Path
from typing import Any

from fastapi import FastAPI, File, HTTPException, Query, UploadFile, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse, PlainTextResponse
from starlette.websockets import WebSocketState

from .audio_processor import AudioProcessor, ProcessorConfig, _pcm16_rms
from .backends import FasterWhisperBackend, MultiplexAsrBackend, TranscriptionError, VibeVoiceBackend
from .protocol import ClientAudioMessage, ClientConfigMessage, ClientEndMessage, ProtocolError, parse_client_message
from .vad import SpeechChunkClassifier

# ======================================================================
# Constants
# ======================================================================

SAMPLE_RATE = 16_000
BYTES_PER_SAMPLE = 2
BYTES_PER_SECOND = SAMPLE_RATE * BYTES_PER_SAMPLE

# ======================================================================
# Configuration
# ======================================================================


def _env_int(name: str, default: int, lo: int, hi: int) -> int:
    raw = os.getenv(name)
    if raw is None:
        return default
    try:
        return max(lo, min(hi, int(raw)))
    except ValueError:
        return default


def _env_float(name: str, default: float, lo: float, hi: float) -> float:
    raw = os.getenv(name)
    if raw is None:
        return default
    try:
        return max(lo, min(hi, float(raw)))
    except ValueError:
        return default


def _env_bool(name: str, default: bool) -> bool:
    raw = os.getenv(name)
    if raw is None:
        return default
    n = raw.strip().lower()
    if n in {"1", "true", "yes", "on"}:
        return True
    if n in {"0", "false", "no", "off"}:
        return False
    return default


def _env_choice(name: str, default: str, allowed: set[str]) -> str:
    raw = os.getenv(name)
    if raw is None:
        return default
    n = raw.strip().lower()
    return n if n in allowed else default


class ServiceConfig:
    """All environment-driven configuration in one place."""

    def __init__(self) -> None:
        # Model
        self.model_name: str = os.getenv("MODEL", "base")
        self.device: str = os.getenv("SPEECH_DEVICE", "cpu")
        self.compute_type: str = os.getenv("SPEECH_COMPUTE_TYPE", "int8")
        self.cpu_threads: int = _env_int("SPEECH_CPU_THREADS", 4, 1, 32)
        self.num_workers: int = _env_int("SPEECH_NUM_WORKERS", 1, 1, 8)

        # Session limits
        self.max_concurrent_sessions: int = _env_int("MAX_CONCURRENT_SESSIONS", 10, 1, 1000)
        self.idle_timeout_sec: int = _env_int("CLIENT_IDLE_TIMEOUT_SEC", 60, 5, 300)
        self.transcribe_timeout_sec: int = _env_int("TRANSCRIBE_TIMEOUT_SEC", 15, 1, 300)

        # VAD
        self.vad_backend: str = os.getenv("SPEECH_VAD_BACKEND", "silero").strip().lower() or "silero"
        self.vad_fallback: str = os.getenv("SPEECH_VAD_FALLBACK", "energy").strip().lower() or "energy"
        self.vad_silence_ms: int = _env_int("VAD_SILENCE_MS", 500, 10, 5000)
        self.vad_energy_threshold: float = _env_float("VAD_ENERGY_THRESHOLD", 0.006, 0.0001, 1.0)
        self.vad_min_speech_ms: int = _env_int("VAD_MIN_SPEECH_MS", 250, 10, 5000)
        self.vad_min_speech_ratio: float = _env_float("VAD_MIN_SPEECH_RATIO", 0.25, 0.05, 1.0)
        self.vad_dynamic_multiplier: float = _env_float("VAD_DYNAMIC_THRESHOLD_MULTIPLIER", 2.4, 1.0, 10.0)

        # Streaming
        self.decode_interval_ms: int = _env_int("STREAM_DECODE_INTERVAL_MS", 300, 10, 10000)
        self.decode_overlap_ms: int = _env_int("STREAM_DECODE_OVERLAP_MS", 400, 0, 5000)
        self.max_active_window_ms: int = _env_int("STREAM_MAX_ACTIVE_WINDOW_MS", 5000, 1000, 30000)
        self.commit_agreement_passes: int = _env_int("STREAM_COMMIT_AGREEMENT_PASSES", 1, 1, 5)
        self.max_buffer_ms: int = _env_int("MAX_AUDIO_BUFFER_MS_PER_CONN", 15000, 1000, 300000)

        # Quality
        self.strict_quality: bool = _env_bool("STRICT_QUALITY_MODE", False)

    def to_processor_config(self) -> ProcessorConfig:
        return ProcessorConfig(
            vad_silence_ms=self.vad_silence_ms,
            vad_energy_threshold=self.vad_energy_threshold,
            vad_min_speech_ms=self.vad_min_speech_ms,
            vad_min_speech_ratio=self.vad_min_speech_ratio,
            vad_dynamic_threshold_multiplier=self.vad_dynamic_multiplier,
            decode_interval_ms=self.decode_interval_ms,
            decode_overlap_ms=self.decode_overlap_ms,
            max_active_window_ms=self.max_active_window_ms,
            commit_agreement_passes=self.commit_agreement_passes,
            max_buffer_ms=self.max_buffer_ms,
            transcribe_timeout_sec=self.transcribe_timeout_sec,
        )


# ======================================================================
# Global singletons
# ======================================================================

cfg = ServiceConfig()
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("speech")

app = FastAPI(title="speech-service-v2")
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=False,
    allow_methods=["*"],
    allow_headers=["*"],
)

_asr = MultiplexAsrBackend(
    faster_whisper_backend=FasterWhisperBackend(
        logger=logger,
        device=cfg.device,
        compute_type=cfg.compute_type,
        cpu_threads=cfg.cpu_threads,
        num_workers=cfg.num_workers,
        download_root=None,
        strict_quality_mode=cfg.strict_quality,
    ),
    vibevoice_backend=VibeVoiceBackend(
        logger=logger,
        device=cfg.device,
        compute_type=cfg.compute_type,
        download_root=None,
    ),
)

_vad = SpeechChunkClassifier(
    logger=logger,
    primary_backend=cfg.vad_backend,
    fallback_backend=cfg.vad_fallback,
    sample_rate=SAMPLE_RATE,
)

# Runtime state
_model_loaded = False
_startup_state = "starting"
_model_failure: str | None = None
_active_sessions: set[str] = set()
_sessions_lock = asyncio.Lock()
_load_task: asyncio.Task[None] | None = None

# ======================================================================
# Model loading
# ======================================================================


async def _ensure_model() -> None:
    global _load_task, _model_loaded, _startup_state, _model_failure

    if _model_loaded and _asr.is_model_ready(cfg.model_name):
        return
    if _startup_state == "startup_failed":
        return
    if _load_task and not _load_task.done():
        return
    if not _asr.runtime_available:
        _startup_state = "startup_failed"
        _model_failure = "ASR runtime not installed"
        return

    _startup_state = "model_loading"
    _load_task = asyncio.create_task(_load_model())


async def _load_model() -> None:
    global _model_loaded, _startup_state, _model_failure
    backend = _asr.backend_name(cfg.model_name)
    try:
        logger.info("model_load_start model=%s backend=%s", cfg.model_name, backend)
        await _asr.load_model(cfg.model_name)
        _model_loaded = _asr.is_model_ready(cfg.model_name)
        _startup_state = "ready" if _model_loaded else "startup_failed"
        _model_failure = None if _model_loaded else "model did not load"
        logger.info("model_load_complete model=%s ready=%s", cfg.model_name, _model_loaded)
    except Exception as exc:
        _model_loaded = False
        _startup_state = "startup_failed"
        _model_failure = str(exc)
        logger.exception("model_load_failed model=%s error=%s", cfg.model_name, exc)


# ======================================================================
# Lifecycle hooks
# ======================================================================


@app.on_event("startup")
async def on_startup() -> None:
    await _ensure_model()


@app.on_event("shutdown")
async def on_shutdown() -> None:
    if _load_task and not _load_task.done():
        _load_task.cancel()
        await asyncio.gather(_load_task, return_exceptions=True)


# ======================================================================
# Health endpoints
# ======================================================================


@app.get("/health")
async def health() -> dict[str, Any]:
    await _ensure_model()
    return {
        "status": "ok",
        "startupState": _startup_state,
        "modelLoaded": _model_loaded and _asr.is_model_ready(cfg.model_name),
        "asrBackend": _asr.backend_name(cfg.model_name),
    }


@app.get("/health/ready")
async def health_ready() -> JSONResponse:
    await _ensure_model()
    async with _sessions_lock:
        active = len(_active_sessions)
    ready = _model_loaded and _asr.is_model_ready(cfg.model_name)
    can_accept = ready and active < cfg.max_concurrent_sessions
    body = {
        "status": "ok" if can_accept else ("not_ready" if not ready else "at_capacity"),
        "modelLoaded": ready,
        "asrBackend": _asr.backend_name(cfg.model_name),
        "failureReason": _model_failure if not ready else None,
        "failureDetail": _model_failure,
        "startupState": _startup_state,
        "activeSessions": active,
        "maxConcurrentSessions": cfg.max_concurrent_sessions,
    }
    return JSONResponse(status_code=200 if can_accept else 503, content=body)


@app.get("/health/diagnostics")
async def health_diagnostics() -> JSONResponse:
    await _ensure_model()
    async with _sessions_lock:
        active = len(_active_sessions)
    ready = _model_loaded and _asr.is_model_ready(cfg.model_name)
    return JSONResponse(content={
        "model": cfg.model_name,
        "asr_backend": _asr.backend_name(cfg.model_name),
        "model_ready": ready,
        "startup_state": _startup_state,
        "compute_type": cfg.compute_type,
        "device": cfg.device,
        "audio_input_contract": "pcm_s16le/16000hz/mono",
        "live_input_sample_rate": SAMPLE_RATE,
        "live_input_channels": 1,
        "live_input_chunk_ms": 200,
        "stream_decode_interval_ms": cfg.decode_interval_ms,
        "stream_commit_agreement_passes": cfg.commit_agreement_passes,
        "vad_min_speech_ms": cfg.vad_min_speech_ms,
        "vad_silence_ms": cfg.vad_silence_ms,
        "vad_backend": _vad.active_backend,
        "silero_available": _vad.silero_available,
        "strict_quality_mode": cfg.strict_quality,
        "active_sessions": active,
        "max_sessions": cfg.max_concurrent_sessions,
        # Placeholders for compatibility with frontend diagnostics panel
        "avg_transcribe_latency_ms": 0,
        "p95_transcribe_latency_ms": 0,
        "total_final_segments": 0,
        "total_partial_segments": 0,
        "total_connections": 0,
        "total_errors": 0,
        "vad_voiced_chunks": 0,
        "vad_rejected_chunks": 0,
        "filtered_decode_results_total": 0,
        "empty_decode_results_total": 0,
        "duplicate_finals_suppressed_total": 0,
        "uptime_sec": 0,
    })


# ======================================================================
# File upload endpoint
# ======================================================================


@app.post("/transcribe")
async def transcribe_upload(
    file: UploadFile = File(...),
    language: str = Query("tr", alias="language"),
) -> JSONResponse:
    await _ensure_model()
    lang = (language or "tr").strip().lower()
    if lang not in {"en", "tr"}:
        raise HTTPException(400, f"Unsupported language '{language}'.")
    if not _model_loaded or not _asr.is_model_ready(cfg.model_name):
        raise HTTPException(503, "Speech model is not ready yet.")

    payload = await file.read()
    if not payload:
        raise HTTPException(400, "Uploaded audio file is empty.")

    try:
        pcm = await _normalize_to_pcm16(payload, filename=file.filename)
    except RuntimeError as exc:
        raise HTTPException(400, str(exc)) from exc

    duration_ms = max(1, int(round((len(pcm) / BYTES_PER_SECOND) * 1000)))
    try:
        result = await asyncio.wait_for(
            _asr.transcribe(
                pcm,
                model_name=cfg.model_name,
                language=lang,
                task="transcribe",
                use_vad=False,
                start_ms=0,
                end_ms=duration_ms,
            ),
            timeout=cfg.transcribe_timeout_sec,
        )
    except asyncio.TimeoutError as exc:
        raise HTTPException(504, "Transcription timed out.") from exc
    except TranscriptionError as exc:
        raise HTTPException(503, exc.detail) from exc

    segments = result.get("segments", [])
    full_text = " ".join(str(s.get("text", "")).strip() for s in segments).strip()
    stats = result.get("stats", {})

    return JSONResponse(content={
        "segments": segments,
        "full_text": full_text,
        "duration_ms": duration_ms,
        "word_count": len(full_text.split()) if full_text else 0,
        "wpm": stats.get("wpm", 0),
        "filler_count": stats.get("filler_count", 0),
        "stats": stats,
    })


# ======================================================================
# WebSocket — live streaming transcription
# ======================================================================


@app.websocket("/ws/transcribe")
async def ws_transcribe(websocket: WebSocket) -> None:
    await _ensure_model()
    await websocket.accept()

    session_id = websocket.query_params.get("session_id", "").strip()
    lang = (websocket.query_params.get("lang", "en") or "en").lower()
    if lang not in {"en", "tr"}:
        lang = "en"
    conn_id = uuid.uuid4().hex[:12]

    # --- Guard: model not ready ---
    if not _model_loaded or not _asr.is_model_ready(cfg.model_name):
        await _ws_error(websocket, "model_not_ready", "Model is not ready yet.", retryable=True)
        await websocket.close(code=1013, reason="model not ready")
        return

    # --- Guard: session limit ---
    async with _sessions_lock:
        if len(_active_sessions) >= cfg.max_concurrent_sessions:
            await _ws_error(websocket, "at_capacity", "Server at capacity.", retryable=True)
            await websocket.close(code=1013, reason="at capacity")
            return
        _active_sessions.add(conn_id)

    logger.info("ws_connected session=%s conn=%s lang=%s", session_id, conn_id, lang)

    processor: AudioProcessor | None = None
    stop = asyncio.Event()

    try:
        # --- Wait for config message ---
        configured = False
        while not configured and not stop.is_set():
            try:
                raw = await asyncio.wait_for(websocket.receive_text(), timeout=cfg.idle_timeout_sec)
            except (asyncio.TimeoutError, WebSocketDisconnect):
                return

            try:
                msg = parse_client_message(raw, default_model=cfg.model_name, default_language=lang)
            except ProtocolError as ex:
                await _ws_error(websocket, ex.error_code, ex.detail, retryable=False)
                return

            if not isinstance(msg, ClientConfigMessage):
                await _ws_error(websocket, "config_required", "Send config first.", retryable=False)
                return

            try:
                await _asr.load_model(msg.config.model)
            except TranscriptionError as ex:
                await _ws_error(websocket, ex.error_code, ex.detail, retryable=False)
                return

            # --- Create processor ---
            processor = AudioProcessor(
                cfg=cfg.to_processor_config(),
                asr=_asr,
                vad=_vad,
                model_name=msg.config.model,
                language=msg.config.language,
                task=msg.config.task,
                use_vad=msg.config.use_vad,
                logger=logger,
                send_partial=lambda text, t_ms: _ws_send(websocket, {"type": "partial", "text": text, "t_ms": t_ms}),
                send_final=lambda segs, stats: _ws_send(websocket, {"type": "final", "segments": segs, "stats": stats}),
                send_notice=lambda text: _ws_send(websocket, {"type": "partial_status", "text": text}),
            )

            await websocket.send_json({
                "type": "ready",
                "session": {
                    "language": msg.config.language,
                    "model": msg.config.model,
                    "task": msg.config.task,
                    "use_vad": msg.config.use_vad,
                },
            })
            configured = True
            logger.info(
                "ws_configured session=%s conn=%s model=%s lang=%s vad=%s",
                session_id, conn_id, msg.config.model, msg.config.language, msg.config.use_vad,
            )

        assert processor is not None

        # --- Main audio loop ---
        while not stop.is_set():
            try:
                raw = await asyncio.wait_for(websocket.receive_text(), timeout=cfg.idle_timeout_sec)
            except asyncio.TimeoutError:
                logger.warning("ws_idle_timeout conn=%s", conn_id)
                await _ws_error(websocket, "idle_timeout", "No messages received.", retryable=False)
                break
            except WebSocketDisconnect:
                break

            try:
                msg = parse_client_message(raw, default_model=cfg.model_name, default_language=lang)
            except ProtocolError as ex:
                await _ws_error(websocket, ex.error_code, ex.detail, retryable=False)
                break

            if isinstance(msg, ClientAudioMessage):
                try:
                    audio = base64.b64decode(msg.data_b64, validate=True)
                except (binascii.Error, ValueError):
                    continue
                chunk_ms = max(1, int(round((len(audio) / BYTES_PER_SECOND) * 1000)))
                await processor.feed_chunk(audio, chunk_ms)

            elif isinstance(msg, ClientEndMessage):
                await processor.finalize()
                break

            elif isinstance(msg, ClientConfigMessage):
                await _ws_error(websocket, "duplicate_config", "Already configured.", retryable=False)
                break

    except Exception:
        logger.exception("ws_error conn=%s", conn_id)
    finally:
        try:
            if websocket.application_state != WebSocketState.DISCONNECTED:
                await websocket.close()
        except Exception:
            pass
        async with _sessions_lock:
            _active_sessions.discard(conn_id)
        logger.info(
            "ws_closed conn=%s decodes=%d partials=%d finals=%d",
            conn_id,
            processor.decode_count if processor else 0,
            processor.partial_count if processor else 0,
            processor.final_count if processor else 0,
        )


# ======================================================================
# Metrics endpoint (compatible with old format)
# ======================================================================

@app.get("/metrics")
async def metrics() -> PlainTextResponse:
    async with _sessions_lock:
        active = len(_active_sessions)
    lines = [
        "# TYPE speech_active_sessions gauge",
        f"speech_active_sessions {active}",
    ]
    return PlainTextResponse(content="\n".join(lines) + "\n", media_type="text/plain; version=0.0.4")


# ======================================================================
# Helpers
# ======================================================================


async def _ws_send(websocket: WebSocket, payload: dict[str, Any]) -> None:
    if websocket.application_state == WebSocketState.DISCONNECTED:
        return
    try:
        await websocket.send_json(payload)
    except Exception:
        pass


async def _ws_error(ws: WebSocket, code: str, detail: str, *, retryable: bool) -> None:
    await _ws_send(ws, {"type": "error", "error": code, "detail": detail, "retryable": retryable})


async def _normalize_to_pcm16(payload: bytes, *, filename: str | None) -> bytes:
    suffix = Path((filename or "").strip()).suffix.lower() or ".bin"
    with tempfile.TemporaryDirectory(prefix="speech-") as d:
        inp = Path(d) / f"input{suffix}"
        inp.write_bytes(payload)
        try:
            proc = await asyncio.create_subprocess_exec(
                "ffmpeg", "-nostdin", "-v", "error",
                "-i", str(inp),
                "-f", "s16le", "-acodec", "pcm_s16le", "-ac", "1", "-ar", str(SAMPLE_RATE),
                "pipe:1",
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
            )
        except OSError as exc:
            raise RuntimeError("ffmpeg is not available.") from exc
        stdout, stderr = await proc.communicate()
    if proc.returncode != 0 or not stdout:
        raise RuntimeError(stderr.decode("utf-8", errors="ignore").strip() or "ffmpeg decode failed.")
    return stdout
