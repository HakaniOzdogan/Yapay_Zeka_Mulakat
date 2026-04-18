from __future__ import annotations

import asyncio
import time
from dataclasses import dataclass, field
from typing import Any

from .protocol import SessionConfig


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
    partial_job_pending: bool = False
    last_partial_enqueued_at_ms: int = 0
    last_partial_text: str = ""
    last_partial_t_ms: int = 0

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
