"""Speech Service — batch HTTP transkripsiyon.

Yalnızca /transcribe (POST) endpoint'i aktif.
WebSocket streaming kaldırıldı.
"""

from __future__ import annotations

import asyncio
import logging
import os
import tempfile
from pathlib import Path
from typing import Any

from fastapi import FastAPI, File, HTTPException, Query, UploadFile
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse, PlainTextResponse

from .backends import FasterWhisperBackend, TranscriptionError

# ======================================================================
# Sabitler
# ======================================================================

SAMPLE_RATE = 16_000
BYTES_PER_SAMPLE = 2
BYTES_PER_SECOND = SAMPLE_RATE * BYTES_PER_SAMPLE


# ======================================================================
# Yapılandırma
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
    return raw.strip().lower() in {"1", "true", "yes", "on"}


class ServiceConfig:
    def __init__(self) -> None:
        self.model_name: str = os.getenv("MODEL", "large-v3-turbo")
        self.device: str = os.getenv("SPEECH_DEVICE", "cpu")
        self.compute_type: str = os.getenv("SPEECH_COMPUTE_TYPE", "int8")
        self.cpu_threads: int = _env_int("SPEECH_CPU_THREADS", 4, 1, 32)
        self.num_workers: int = _env_int("SPEECH_NUM_WORKERS", 1, 1, 8)
        self.transcribe_timeout_sec: int = _env_int("TRANSCRIBE_TIMEOUT_SEC", 60, 5, 300)
        self.strict_quality: bool = _env_bool("STRICT_QUALITY_MODE", False)


# ======================================================================
# Global singletons
# ======================================================================

cfg = ServiceConfig()
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("speech")

app = FastAPI(title="speech-service")
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=False,
    allow_methods=["*"],
    allow_headers=["*"],
)

_asr = FasterWhisperBackend(
    logger=logger,
    device=cfg.device,
    compute_type=cfg.compute_type,
    cpu_threads=cfg.cpu_threads,
    num_workers=cfg.num_workers,
    download_root=None,
    strict_quality_mode=cfg.strict_quality,
)

_model_loaded = False
_startup_state = "starting"
_model_failure: str | None = None

# Limit concurrent Whisper GPU inference to 1.
# Concurrent GPU calls cause CUDA contention; the second call silently fails or times out.
_transcribe_semaphore = asyncio.Semaphore(1)


# ======================================================================
# Model yükleme
# ======================================================================

async def _ensure_model() -> None:
    global _model_loaded, _startup_state, _model_failure

    if _model_loaded and _asr.is_model_ready(cfg.model_name):
        return
    if _startup_state == "startup_failed":
        return
    if _startup_state == "model_loading":
        return
    if not _asr.runtime_available:
        _startup_state = "startup_failed"
        _model_failure = "faster_whisper is not installed"
        return

    _startup_state = "model_loading"
    asyncio.create_task(_load_model())


async def _load_model() -> None:
    global _model_loaded, _startup_state, _model_failure
    try:
        logger.info("model_load_start model=%s", cfg.model_name)
        await _asr.load_model(cfg.model_name)
        _model_loaded = _asr.is_model_ready(cfg.model_name)
        _startup_state = "ready" if _model_loaded else "startup_failed"
        _model_failure = None if _model_loaded else "model did not load"
        logger.info("model_load_complete model=%s ready=%s", cfg.model_name, _model_loaded)
    except Exception as exc:
        _model_loaded = False
        _startup_state = "startup_failed"
        _model_failure = str(exc)
        logger.exception("model_load_failed model=%s", cfg.model_name)


# ======================================================================
# Lifecycle
# ======================================================================

@app.on_event("startup")
async def on_startup() -> None:
    await _ensure_model()


# ======================================================================
# Sağlık endpoint'leri
# ======================================================================

@app.get("/health")
async def health() -> dict[str, Any]:
    await _ensure_model()
    return {
        "status": "ok",
        "startupState": _startup_state,
        "modelLoaded": _model_loaded and _asr.is_model_ready(cfg.model_name),
        "asrBackend": "faster_whisper",
    }


@app.get("/health/ready")
async def health_ready() -> JSONResponse:
    await _ensure_model()
    ready = _model_loaded and _asr.is_model_ready(cfg.model_name)
    body = {
        "status": "ok" if ready else ("startup_failed" if _startup_state == "startup_failed" else "not_ready"),
        "modelLoaded": ready,
        "asrBackend": "faster_whisper",
        "failureReason": _model_failure if not ready else None,
        "failureDetail": _model_failure,
        "startupState": _startup_state,
    }
    return JSONResponse(status_code=200 if ready else 503, content=body)


@app.get("/health/diagnostics")
async def health_diagnostics() -> JSONResponse:
    await _ensure_model()
    ready = _model_loaded and _asr.is_model_ready(cfg.model_name)
    return JSONResponse(content={
        "model": cfg.model_name,
        "asr_backend": "faster_whisper",
        "model_ready": ready,
        "startup_state": _startup_state,
        "compute_type": cfg.compute_type,
        "device": cfg.device,
        "audio_input_contract": "pcm_s16le/16000hz/mono",
        "live_input_sample_rate": SAMPLE_RATE,
        "live_input_channels": 1,
        "live_input_chunk_ms": 0,
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
        "vad_backend": "none",
        "silero_available": False,
        "strict_quality_mode": cfg.strict_quality,
        "active_sessions": 0,
        "max_sessions": 1,
    })


# ======================================================================
# Batch transkripsiyon endpoint'i
# ======================================================================

@app.post("/transcribe")
async def transcribe_upload(
    file: UploadFile = File(...),
    language: str = Query("auto", alias="language"),
    quality: str = Query("fast", alias="quality"),
) -> JSONResponse:
    await _ensure_model()

    # "auto" or empty → let Whisper detect the language automatically.
    # Explicit codes (e.g. "tr", "en") are passed through as hints.
    raw = (language or "auto").strip().lower()
    lang: str | None = None if raw == "auto" else raw

    # quality=accurate uses higher beam_size/best_of for better results at the cost of speed.
    q = (quality or "fast").strip().lower()
    req_beam_size = 5 if q == "accurate" else None
    req_best_of   = 5 if q == "accurate" else None

    if not _model_loaded or not _asr.is_model_ready(cfg.model_name):
        raise HTTPException(503, "Konuşma modeli henüz hazır değil.")

    payload = await file.read()
    if not payload:
        raise HTTPException(400, "Yüklenen ses dosyası boş.")

    try:
        pcm = await _normalize_to_pcm16(payload, filename=file.filename or "audio.webm")
    except RuntimeError as exc:
        raise HTTPException(400, str(exc)) from exc

    duration_ms = max(1, int(round((len(pcm) / BYTES_PER_SECOND) * 1000)))

    try:
        async with _transcribe_semaphore:
            result = await asyncio.wait_for(
                _asr.transcribe(
                    pcm,
                    model_name=cfg.model_name,
                    language=lang,
                    task="transcribe",
                    use_vad=False,
                    start_ms=0,
                    end_ms=duration_ms,
                    beam_size=req_beam_size,
                    best_of=req_best_of,
                ),
                timeout=cfg.transcribe_timeout_sec,
            )
    except asyncio.TimeoutError as exc:
        raise HTTPException(504, "Transkripsiyon zaman aşımına uğradı.") from exc
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
# Metrics endpoint
# ======================================================================

@app.get("/metrics")
async def metrics() -> PlainTextResponse:
    lines = [
        "# TYPE speech_active_sessions gauge",
        "speech_active_sessions 0",
    ]
    return PlainTextResponse(content="\n".join(lines) + "\n", media_type="text/plain; version=0.0.4")


# ======================================================================
# Yardımcı fonksiyon
# ======================================================================

async def _normalize_to_pcm16(payload: bytes, *, filename: str) -> bytes:
    suffix = Path(filename.strip()).suffix.lower() or ".bin"
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
            raise RuntimeError("ffmpeg bulunamadı.") from exc
        stdout, stderr = await proc.communicate()
    if proc.returncode != 0 or not stdout:
        raise RuntimeError(stderr.decode("utf-8", errors="ignore").strip() or "ffmpeg dönüşümü başarısız.")
    return stdout
