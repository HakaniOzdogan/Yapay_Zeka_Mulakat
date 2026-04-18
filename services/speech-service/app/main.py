from __future__ import annotations

import asyncio
import base64
import binascii
import json
import logging
import math
import os
import time
import uuid
from dataclasses import dataclass
from typing import Any

from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.responses import JSONResponse, PlainTextResponse
from starlette.websockets import WebSocketState

from .backends import FasterWhisperBackend, TranscriptionError
from .protocol import ClientAudioMessage, ClientConfigMessage, ClientEndMessage, ProtocolError, parse_client_message
from .session import TranscriptionSession

SAMPLE_RATE = 16_000
BYTES_PER_SAMPLE = 2
BYTES_PER_SECOND = SAMPLE_RATE * BYTES_PER_SAMPLE


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
    partial_emit_interval_ms: int
    model_name: str

    @staticmethod
    def from_env() -> "ServiceConfig":
        return ServiceConfig(
            max_concurrent_sessions=_env_int("MAX_CONCURRENT_SESSIONS", 10, 1, 1000),
            max_queue_messages_per_conn=_env_int("MAX_QUEUE_MESSAGES_PER_CONN", 50, 1, 5000),
            max_audio_buffer_ms_per_conn=_env_int("MAX_AUDIO_BUFFER_MS_PER_CONN", 15000, 1000, 300000),
            client_idle_timeout_sec=_env_int("CLIENT_IDLE_TIMEOUT_SEC", 60, 5, 300),
            backpressure_queue_wait_ms=_env_int("BACKPRESSURE_QUEUE_WAIT_MS", 1000, 100, 10000),
            transcribe_timeout_sec=_env_int("TRANSCRIBE_TIMEOUT_SEC", 20, 1, 300),
            vad_silence_ms=_env_int("VAD_SILENCE_MS", 1000, 200, 5000),
            vad_energy_threshold=_env_float("VAD_ENERGY_THRESHOLD", 0.005, 0.0001, 1.0),
            partial_emit_interval_ms=_env_int("PARTIAL_EMIT_INTERVAL_MS", 1000, 200, 10000),
            model_name=os.getenv("MODEL", "tiny"),
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


def _json_log(event: str, **fields: Any) -> str:
    payload: dict[str, Any] = {"event": event, **fields}
    return json.dumps(payload, ensure_ascii=False, separators=(",", ":"))


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
        self.metrics = MetricsRegistry()
        self._sessions_lock = asyncio.Lock()
        self._active_connection_ids: set[str] = set()
        self._connection_backlogs: dict[str, int] = {}

    async def mark_model_loaded(self, loaded: bool) -> None:
        self.model_loaded = loaded

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


@dataclass
class InboundChunk:
    kind: str
    seq: int | None = None
    audio_bytes: bytes | None = None
    chunk_ms: int = 0


@dataclass(frozen=True)
class TranscribeJob:
    kind: str
    audio_bytes: bytes
    start_ms: int
    end_ms: int
    reason: str


config = ServiceConfig.from_env()
runtime = RuntimeState(config)

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("speech-service")

app = FastAPI(title="speech-service")

_asr_backend = FasterWhisperBackend(logger=logger)


@app.on_event("startup")
async def on_startup() -> None:
    await runtime.mark_model_loaded(False)
    if not _asr_backend.runtime_available:
        logger.warning(_json_log("whisper_unavailable", reason="faster_whisper not installed"))
        return

    try:
        logger.info(_json_log("whisper_load_start", model=config.model_name, backend="faster_whisper"))
        await _asr_backend.load_model(config.model_name)
        logger.info(_json_log("whisper_load_complete", model=config.model_name, backend="faster_whisper"))
    except TranscriptionError as exc:
        logger.error(_json_log("whisper_load_failed", model=config.model_name, backend="faster_whisper", error=exc.detail))

    await runtime.mark_model_loaded(_asr_backend.is_model_ready(config.model_name))
    logger.info(
        _json_log(
            "startup_complete",
            model=config.model_name,
            whisper_ready=_asr_backend.is_model_ready(config.model_name),
            max_concurrent_sessions=config.max_concurrent_sessions,
            max_queue_messages_per_conn=config.max_queue_messages_per_conn,
            max_audio_buffer_ms_per_conn=config.max_audio_buffer_ms_per_conn,
            transcribe_timeout_sec=config.transcribe_timeout_sec,
        )
    )


@app.get("/health")
async def health() -> dict[str, Any]:
    return {"status": "ok"}


@app.get("/health/ready")
async def health_ready() -> JSONResponse:
    active = await runtime.active_sessions()
    model_ready = runtime.model_loaded and _asr_backend.is_model_ready(config.model_name)
    can_accept = model_ready and active < config.max_concurrent_sessions
    body = {
        "status": "ok" if can_accept else ("not_ready" if not model_ready else "at_capacity"),
        "modelLoaded": model_ready,
        "activeSessions": active,
        "maxConcurrentSessions": config.max_concurrent_sessions,
        "uptimeSec": runtime.uptime_sec(),
    }
    return JSONResponse(status_code=200 if can_accept else 503, content=body)


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
    buffer = bytearray()
    buffer_ms = 0
    total_audio_ms = 0
    silence_ms = 0

    async def flush_buffer(reason: str) -> None:
        nonlocal buffer, buffer_ms
        if buffer_ms <= 0 or not buffer:
            return

        start_ms = max(0, total_audio_ms - buffer_ms)
        end_ms = total_audio_ms
        await session.transcribe_queue.put(
            TranscribeJob(kind="final", audio_bytes=bytes(buffer), start_ms=start_ms, end_ms=end_ms, reason=reason)
        )
        logger.info(
            _json_log(
                "transcribe_job_queued",
                session_id=session.session_id,
                connection_id=session.connection_id,
                language=session.language,
                reason=reason,
                duration_ms=buffer_ms,
            )
        )
        session.last_partial_enqueued_at_ms = 0
        buffer = bytearray()
        buffer_ms = 0

    async def queue_partial_snapshot() -> None:
        if session.partial_job_pending or buffer_ms < config.partial_emit_interval_ms or not buffer:
            return

        if buffer_ms - session.last_partial_enqueued_at_ms < config.partial_emit_interval_ms:
            return

        start_ms = max(0, total_audio_ms - buffer_ms)
        end_ms = total_audio_ms
        await session.transcribe_queue.put(
            TranscribeJob(
                kind="partial",
                audio_bytes=bytes(buffer),
                start_ms=start_ms,
                end_ms=end_ms,
                reason="interval_partial",
            )
        )
        session.partial_job_pending = True
        session.last_partial_enqueued_at_ms = buffer_ms
        logger.info(
            _json_log(
                "partial_job_queued",
                session_id=session.session_id,
                connection_id=session.connection_id,
                language=session.language,
                duration_ms=buffer_ms,
                end_ms=end_ms,
            )
        )

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
                await flush_buffer("client_end")
                session.stop_event.set()
                break

            if msg.kind != "audio" or msg.audio_bytes is None:
                continue

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
            total_audio_ms += msg.chunk_ms

            if buffer_ms + msg.chunk_ms > config.max_audio_buffer_ms_per_conn:
                logger.warning(
                    _json_log(
                        "audio_buffer_limit_exceeded",
                        session_id=session.session_id,
                        connection_id=session.connection_id,
                        language=session.language,
                        buffer_ms=buffer_ms,
                        incoming_chunk_ms=msg.chunk_ms,
                        max_buffer_ms=config.max_audio_buffer_ms_per_conn,
                    )
                )
                await flush_buffer("buffer_limit")

            buffer.extend(msg.audio_bytes)
            buffer_ms += msg.chunk_ms

            await queue_partial_snapshot()

            if session.use_vad and chunk_rms < config.vad_energy_threshold:
                silence_ms += msg.chunk_ms
            else:
                silence_ms = 0

            if session.use_vad and silence_ms >= config.vad_silence_ms and buffer_ms > 0:
                logger.info(
                    _json_log(
                        "vad_finalize",
                        session_id=session.session_id,
                        connection_id=session.connection_id,
                        language=session.language,
                        silence_ms=silence_ms,
                        buffer_ms=buffer_ms,
                    )
                )
                await flush_buffer("vad_silence")
                silence_ms = 0
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

        if job.kind == "partial":
            try:
                partial = await asyncio.wait_for(
                    _transcribe_partial(job.audio_bytes, session, job.start_ms, job.end_ms),
                    timeout=config.transcribe_timeout_sec,
                )
            except asyncio.TimeoutError:
                await runtime.metrics.inc("speech_transcribe_errors_total")
                await runtime.metrics.inc("speech_transcribe_timeouts_total")
                logger.warning(
                    _json_log(
                        "partial_timeout",
                        session_id=session.session_id,
                        connection_id=session.connection_id,
                        language=session.language,
                        timeout_sec=config.transcribe_timeout_sec,
                        reason=job.reason,
                    )
                )
            except TranscriptionError as ex:
                await runtime.metrics.inc("speech_transcribe_errors_total")
                await runtime.metrics.inc("speech_transcribe_model_errors_total")
                logger.warning(
                    _json_log(
                        "partial_error",
                        session_id=session.session_id,
                        connection_id=session.connection_id,
                        language=session.language,
                        reason=job.reason,
                        error=ex.detail,
                        error_code=ex.error_code,
                    )
                )
                if ex.error_code == "model_unavailable":
                    await _send_ws_error(
                        websocket,
                        error_code=ex.error_code,
                        detail=ex.detail,
                        retryable=False,
                    )
                    session.stop_event.set()
                    break
            else:
                partial_text = str(partial.get("text", "")).strip()
                partial_t_ms = int(partial.get("end_ms", job.end_ms))
                if partial_text and (
                    partial_text != session.last_partial_text or partial_t_ms > session.last_partial_t_ms
                ):
                    await websocket.send_json({"type": "partial", "text": partial_text, "t_ms": partial_t_ms})
                    await runtime.metrics.inc("speech_partial_messages_total")
                    session.last_partial_text = partial_text
                    session.last_partial_t_ms = partial_t_ms
                    if not first_partial_sent:
                        first_partial_sent = True
                        elapsed_ms = (time.monotonic() - session.conn_started) * 1000.0
                        await runtime.metrics.observe_partial_latency(elapsed_ms)
            finally:
                session.partial_job_pending = False
            continue

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

        await websocket.send_json({"type": "final", "segments": result["segments"], "stats": result["stats"]})
        await runtime.metrics.inc("speech_final_messages_total")
        session.last_partial_text = ""
        session.last_partial_t_ms = 0
        if not first_final_sent:
            first_final_sent = True
            elapsed_ms = (time.monotonic() - session.conn_started) * 1000.0
            await runtime.metrics.observe_final_latency(elapsed_ms)

        logger.info(
            _json_log(
                "final_emitted",
                session_id=session.session_id,
                connection_id=session.connection_id,
                language=session.language,
                reason=job.reason,
                start_ms=job.start_ms,
                end_ms=job.end_ms,
                segments_count=len(result["segments"]),
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


async def _transcribe_partial(
    audio_bytes: bytes,
    session: TranscriptionSession,
    start_ms: int,
    end_ms: int,
) -> dict[str, Any]:
    return await _asr_backend.transcribe_partial(
        audio_bytes,
        model_name=session.model,
        language=session.language,
        task=session.task,
        use_vad=session.use_vad,
        start_ms=start_ms,
        end_ms=end_ms,
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
