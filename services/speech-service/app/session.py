from __future__ import annotations

import asyncio
import time
from dataclasses import dataclass, field
from typing import Any

from .protocol import SessionConfig
from .streaming_state import StreamingCommitState


@dataclass
class StreamingWindowState:
    active_audio: bytearray = field(default_factory=bytearray)
    start_ms: int = 0
    duration_ms: int = 0
    voiced_ms: int = 0
    pending_voiced_ms: int = 0
    trailing_silence_ms: int = 0
    recent_voiced_ms: int = 0
    recent_unvoiced_ms: int = 0
    speech_started: bool = False

    def reset(self) -> None:
        self.active_audio = bytearray()
        self.start_ms = 0
        self.duration_ms = 0
        self.voiced_ms = 0
        self.pending_voiced_ms = 0
        self.trailing_silence_ms = 0
        self.recent_voiced_ms = 0
        self.recent_unvoiced_ms = 0
        self.speech_started = False


@dataclass
class TranscriptionSession:
    session_id: str
    connection_id: str
    default_language: str
    default_model: str
    inbound_queue: asyncio.Queue[Any]
    transcribe_queue: asyncio.Queue[Any]
    stop_event: asyncio.Event = field(default_factory=asyncio.Event)
    conn_started: float = field(default_factory=time.monotonic)
    config: SessionConfig | None = None
    last_partial_text: str = ""
    last_partial_t_ms: int = 0
    last_partial_emitted_at_ms: int = 0
    stream_state: StreamingWindowState = field(default_factory=StreamingWindowState)
    commit_state: StreamingCommitState = field(default_factory=StreamingCommitState)
    vad_history_audio: bytearray = field(default_factory=bytearray)
    vad_history_ms: int = 0
    received_audio_chunks: int = 0
    decode_jobs_queued: int = 0
    suspected_speech_ms_without_decode: int = 0
    stall_warning_emitted: bool = False
    first_decode_job_at_ms: int = 0
    partials_seen: bool = False
    max_partial_text_length: int = 0
    total_final_text_length: int = 0
    last_final_signature: str = ""

    @property
    def is_configured(self) -> bool:
        return self.config is not None

    @property
    def language(self) -> str:
        return self.config.language if self.config else self.default_language

    @property
    def model(self) -> str:
        return self.config.model if self.config else self.default_model

    @property
    def task(self) -> str:
        return self.config.task if self.config else "transcribe"

    @property
    def use_vad(self) -> bool:
        return self.config.use_vad if self.config else True

    def apply_config(self, config: SessionConfig) -> None:
        self.config = config
