from __future__ import annotations

from typing import Any

from .base import BaseAsrBackend
from .vibevoice_backend import is_vibevoice_model_name


class MultiplexAsrBackend(BaseAsrBackend):
    def __init__(self, *, faster_whisper_backend: BaseAsrBackend, vibevoice_backend: BaseAsrBackend) -> None:
        self._faster_whisper_backend = faster_whisper_backend
        self._vibevoice_backend = vibevoice_backend

    @property
    def runtime_available(self) -> bool:
        return self._faster_whisper_backend.runtime_available or self._vibevoice_backend.runtime_available

    def backend_name(self, model_name: str) -> str:
        return "vibevoice" if is_vibevoice_model_name(model_name) else "faster_whisper"

    def _select_backend(self, model_name: str) -> BaseAsrBackend:
        return self._vibevoice_backend if is_vibevoice_model_name(model_name) else self._faster_whisper_backend

    async def load_model(self, model_name: str) -> None:
        await self._select_backend(model_name).load_model(model_name)

    def is_model_ready(self, model_name: str) -> bool:
        return self._select_backend(model_name).is_model_ready(model_name)

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
        return await self._select_backend(model_name).transcribe(
            audio_bytes,
            model_name=model_name,
            language=language,
            task=task,
            use_vad=use_vad,
            start_ms=start_ms,
            end_ms=end_ms,
        )

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
        return await self._select_backend(model_name).transcribe_partial(
            audio_bytes,
            model_name=model_name,
            language=language,
            task=task,
            use_vad=use_vad,
            start_ms=start_ms,
            end_ms=end_ms,
        )
