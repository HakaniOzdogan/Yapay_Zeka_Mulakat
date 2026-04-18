from __future__ import annotations

import json
from dataclasses import dataclass
from typing import Any

SUPPORTED_LANGUAGES = {"en", "tr"}
SUPPORTED_TASKS = {"transcribe", "translate"}


class ProtocolError(ValueError):
    def __init__(self, error_code: str, detail: str) -> None:
        super().__init__(detail)
        self.error_code = error_code
        self.detail = detail


@dataclass(frozen=True)
class SessionConfig:
    language: str
    model: str
    task: str
    use_vad: bool


@dataclass(frozen=True)
class ClientConfigMessage:
    config: SessionConfig


@dataclass(frozen=True)
class ClientAudioMessage:
    seq: int | None
    data_b64: str


@dataclass(frozen=True)
class ClientEndMessage:
    pass


ClientMessage = ClientConfigMessage | ClientAudioMessage | ClientEndMessage


def parse_client_message(raw: str, default_model: str, default_language: str) -> ClientMessage:
    try:
        payload = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise ProtocolError("invalid_json", "Client message is not valid JSON.") from exc

    if not isinstance(payload, dict):
        raise ProtocolError("invalid_message", "Client message must be a JSON object.")

    msg_type = payload.get("type")
    if msg_type == "config":
        language = str(payload.get("language") or default_language or "en").lower()
        if language not in SUPPORTED_LANGUAGES:
            raise ProtocolError("unsupported_language", f"Unsupported language '{language}'.")

        model = str(payload.get("model") or default_model).strip()
        if not model:
            raise ProtocolError("invalid_model", "Model must be a non-empty string.")

        task = str(payload.get("task") or "transcribe").lower()
        if task not in SUPPORTED_TASKS:
            raise ProtocolError("unsupported_task", f"Unsupported task '{task}'.")

        use_vad = payload.get("use_vad", True)
        if not isinstance(use_vad, bool):
            raise ProtocolError("invalid_use_vad", "use_vad must be a boolean.")

        return ClientConfigMessage(
            config=SessionConfig(
                language=language,
                model=model,
                task=task,
                use_vad=use_vad,
            )
        )

    if msg_type == "audio":
        data_b64 = payload.get("data_b64", "")
        if not isinstance(data_b64, str) or not data_b64:
            raise ProtocolError("invalid_audio_payload", "Audio message must include a non-empty data_b64 field.")
        return ClientAudioMessage(seq=_safe_int(payload.get("seq")), data_b64=data_b64)

    if msg_type == "end":
        return ClientEndMessage()

    raise ProtocolError("unsupported_message_type", f"Unsupported message type '{msg_type}'.")


def _safe_int(value: Any) -> int | None:
    try:
        return int(value)
    except (TypeError, ValueError):
        return None
