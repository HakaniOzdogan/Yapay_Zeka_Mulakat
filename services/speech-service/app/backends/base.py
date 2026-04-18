from __future__ import annotations

from abc import ABC, abstractmethod
from typing import Any


class TranscriptionError(RuntimeError):
    def __init__(self, error_code: str, detail: str) -> None:
        super().__init__(detail)
        self.error_code = error_code
        self.detail = detail


class ModelUnavailableError(TranscriptionError):
    def __init__(self, detail: str = "Live transcript model is not ready.") -> None:
        super().__init__("model_unavailable", detail)


class TranscriptionModelError(TranscriptionError):
    def __init__(self, detail: str) -> None:
        super().__init__("transcribe_model_error", detail)


class BaseAsrBackend(ABC):
    @property
    @abstractmethod
    def runtime_available(self) -> bool:
        raise NotImplementedError

    @abstractmethod
    async def load_model(self, model_name: str) -> None:
        raise NotImplementedError

    @abstractmethod
    def is_model_ready(self, model_name: str) -> bool:
        raise NotImplementedError

    @abstractmethod
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
        raise NotImplementedError

    @abstractmethod
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
        raise NotImplementedError
