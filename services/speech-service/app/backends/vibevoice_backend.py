from __future__ import annotations

import asyncio
import json
import logging
import os
import tempfile
import wave
from pathlib import Path
from typing import Any

from .base import BaseAsrBackend, ModelUnavailableError, TranscriptionModelError

VIBEVOICE_OFFICIAL_MODEL_ID = "microsoft/VibeVoice-ASR-HF"

try:
    import torch
    from transformers import AutoProcessor, VibeVoiceAsrForConditionalGeneration

    _VIBEVOICE_AVAILABLE = True
except ImportError:
    torch = None  # type: ignore[assignment]
    AutoProcessor = None  # type: ignore[assignment]
    VibeVoiceAsrForConditionalGeneration = None  # type: ignore[assignment]
    _VIBEVOICE_AVAILABLE = False


def is_vibevoice_model_name(model_name: str) -> bool:
    normalized = (model_name or "").strip().lower().rstrip("/")
    if not normalized:
        return False
    if normalized in {
        "vibevoice",
        "vibevoice-asr",
        "vibevoice-asr-hf",
        VIBEVOICE_OFFICIAL_MODEL_ID.lower(),
        "https://huggingface.co/spaces/microsoft/vibevoice-asr",
        "https://huggingface.co/microsoft/vibevoice-asr-hf",
    }:
        return True
    return "vibevoice-asr" in normalized


def resolve_vibevoice_model_name(model_name: str) -> str | None:
    return VIBEVOICE_OFFICIAL_MODEL_ID if is_vibevoice_model_name(model_name) else None


class VibeVoiceBackend(BaseAsrBackend):
    def __init__(
        self,
        *,
        logger: logging.Logger,
        device: str = "auto",
        compute_type: str = "default",
        download_root: str | None = None,
    ) -> None:
        self._logger = logger
        self._device = device
        self._compute_type = compute_type
        self._download_root = download_root
        self._prompt = (os.getenv("SPEECH_CONTEXT_PROMPT") or "").strip() or None
        self._tokenizer_chunk_size = _env_int("SPEECH_VIBEVOICE_TOKENIZER_CHUNK_SIZE", 0, 0, 1_440_000)
        self._models: dict[str, tuple[Any, Any]] = {}
        self._locks: dict[str, asyncio.Lock] = {}

    @property
    def runtime_available(self) -> bool:
        return _VIBEVOICE_AVAILABLE

    async def load_model(self, model_name: str) -> None:
        resolved_model_name = resolve_vibevoice_model_name(model_name)
        if resolved_model_name is None:
            raise TranscriptionModelError(f"Unsupported VibeVoice model '{model_name}'.")

        if not self.runtime_available:
            raise ModelUnavailableError("VibeVoice-ASR runtime dependencies are not installed.")

        if resolved_model_name in self._models:
            return

        lock = self._locks.setdefault(resolved_model_name, asyncio.Lock())
        async with lock:
            if resolved_model_name in self._models:
                return

            self._logger.info(_json_log("backend_model_load_start", backend="vibevoice", model=resolved_model_name))
            loop = asyncio.get_running_loop()
            try:
                processor, model = await loop.run_in_executor(None, lambda: self._create_bundle(resolved_model_name))
            except Exception as exc:
                self._logger.error(
                    _json_log(
                        "backend_model_load_failed",
                        backend="vibevoice",
                        model=resolved_model_name,
                        error=str(exc),
                    )
                )
                raise TranscriptionModelError(f"Failed to load speech model '{resolved_model_name}'.") from exc

            self._models[resolved_model_name] = (processor, model)
            self._logger.info(_json_log("backend_model_load_complete", backend="vibevoice", model=resolved_model_name))

    def is_model_ready(self, model_name: str) -> bool:
        resolved_model_name = resolve_vibevoice_model_name(model_name)
        if resolved_model_name is None:
            return False
        return resolved_model_name in self._models

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
        resolved_model_name = resolve_vibevoice_model_name(model_name)
        if resolved_model_name is None:
            raise TranscriptionModelError(f"Unsupported VibeVoice model '{model_name}'.")

        if not self.runtime_available:
            raise ModelUnavailableError("VibeVoice-ASR runtime dependencies are not installed.")

        bundle = self._models.get(resolved_model_name)
        if bundle is None:
            await self.load_model(resolved_model_name)
            bundle = self._models.get(resolved_model_name)

        if bundle is None:
            raise ModelUnavailableError(f"Speech model '{resolved_model_name}' is not ready.")

        if len(audio_bytes) < 2:
            return {
                "segments": [],
                "stats": {"wpm": 0, "filler_count": 0, "pause_count": 0, "pause_ms": 0},
                "meta": {"backend": "vibevoice"},
            }

        duration_ms = max(1, end_ms - start_ms)
        duration_sec = duration_ms / 1000.0
        processor, model = bundle

        del language
        del task
        del use_vad

        try:
            loop = asyncio.get_running_loop()
            parsed_output, transcription_text = await loop.run_in_executor(
                None,
                lambda: _run_vibevoice_inference(
                    processor=processor,
                    model=model,
                    audio_bytes=audio_bytes,
                    prompt=self._prompt,
                    tokenizer_chunk_size=self._tokenizer_chunk_size or None,
                ),
            )
        except Exception as exc:
            self._logger.warning(
                _json_log(
                    "backend_transcribe_error",
                    backend="vibevoice",
                    model=resolved_model_name,
                    error=str(exc),
                )
            )
            raise TranscriptionModelError("Speech model failed while transcribing audio.") from exc

        segments_out = _build_segments_from_vibevoice_output(
            parsed_output=parsed_output,
            transcription_text=transcription_text,
            start_ms=start_ms,
            end_ms=end_ms,
        )
        all_words = " ".join(segment["text"] for segment in segments_out).split()
        word_count = len(all_words)
        wpm = int(round(word_count * 60.0 / max(duration_sec, 0.1))) if word_count else 0
        filler_count = sum(1 for word in all_words if word.lower().strip(".,!?") in _FILLER_WORDS)

        return {
            "segments": segments_out,
            "stats": {
                "wpm": wpm,
                "filler_count": filler_count,
                "pause_count": 0,
                "pause_ms": 0,
            },
            "meta": {
                "backend": "vibevoice",
                "tokenizer_chunk_size": self._tokenizer_chunk_size or None,
                "prompt_used": bool(self._prompt),
                "structured_segments": len(segments_out),
            },
        }

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
        result = await self.transcribe(
            audio_bytes,
            model_name=model_name,
            language=language,
            task=task,
            use_vad=use_vad,
            start_ms=start_ms,
            end_ms=end_ms,
        )
        text = " ".join(segment["text"] for segment in result["segments"]).strip()
        return {
            "text": text,
            "start_ms": start_ms,
            "end_ms": end_ms,
        }

    def _create_bundle(self, model_name: str) -> tuple[Any, Any]:
        processor = AutoProcessor.from_pretrained(model_name, cache_dir=self._download_root)

        model_kwargs: dict[str, Any] = {"cache_dir": self._download_root}
        torch_dtype = _resolve_torch_dtype(self._device, self._compute_type)
        if torch_dtype is not None:
            model_kwargs["torch_dtype"] = torch_dtype
        if self._device in {"auto", "cuda"}:
            model_kwargs["device_map"] = "auto"

        model = VibeVoiceAsrForConditionalGeneration.from_pretrained(model_name, **model_kwargs)
        if self._device == "cpu":
            model.to("cpu")
        elif self._device not in {"auto", "cuda"}:
            model.to(self._device)
        model.eval()
        return processor, model


def _run_vibevoice_inference(
    *,
    processor: Any,
    model: Any,
    audio_bytes: bytes,
    prompt: str | None,
    tokenizer_chunk_size: int | None,
) -> tuple[Any, str]:
    with tempfile.TemporaryDirectory(prefix="vibevoice-") as temp_dir:
        audio_path = Path(temp_dir) / "input.wav"
        _write_pcm16_wav(audio_path, audio_bytes)

        inputs = processor.apply_transcription_request(audio=str(audio_path), prompt=prompt)
        inputs = _move_inputs_to_model(inputs, model)

        generate_kwargs: dict[str, Any] = {}
        if tokenizer_chunk_size:
            generate_kwargs["tokenizer_chunk_size"] = tokenizer_chunk_size

        output_ids = model.generate(**inputs, **generate_kwargs)
        prompt_length = int(inputs["input_ids"].shape[1])
        generated_ids = output_ids[:, prompt_length:]
        parsed = processor.decode(generated_ids, return_format="parsed")[0]
        text_only = processor.decode(generated_ids, return_format="transcription_only")[0]
        return parsed, str(text_only or "").strip()


def _move_inputs_to_model(inputs: Any, model: Any) -> Any:
    device = getattr(model, "device", None)
    dtype = getattr(model, "dtype", None)
    if device is None:
        return inputs
    try:
        if dtype is not None:
            return inputs.to(device, dtype)
        return inputs.to(device)
    except TypeError:
        return inputs.to(device)


def _build_segments_from_vibevoice_output(
    *,
    parsed_output: Any,
    transcription_text: str,
    start_ms: int,
    end_ms: int,
) -> list[dict[str, Any]]:
    parsed_segments = _normalize_parsed_output(parsed_output)
    segments_out: list[dict[str, Any]] = []

    for item in parsed_segments:
        text = str(item.get("Content") or "").strip()
        if not text:
            continue

        seg_start_ms = start_ms + int(round(_safe_float(item.get("Start")) * 1000))
        seg_end_ms = start_ms + int(round(_safe_float(item.get("End")) * 1000))
        seg_end_ms = max(seg_start_ms, seg_end_ms)
        segments_out.append(
            {
                "start_ms": seg_start_ms,
                "end_ms": min(end_ms, seg_end_ms if seg_end_ms > seg_start_ms else end_ms),
                "text": text,
            }
        )

    if segments_out:
        return segments_out

    text = transcription_text.strip()
    if not text:
        return []
    return [{"start_ms": start_ms, "end_ms": end_ms, "text": text}]


def _normalize_parsed_output(parsed_output: Any) -> list[dict[str, Any]]:
    if isinstance(parsed_output, list):
        return [item for item in parsed_output if isinstance(item, dict)]

    if not isinstance(parsed_output, str):
        return []

    cleaned = parsed_output.strip()
    cleaned = cleaned.removeprefix("<|im_start|>assistant").strip()
    cleaned = cleaned.replace("<|im_end|>", "").replace("<|endoftext|>", "").strip()

    try:
        payload = json.loads(cleaned)
    except json.JSONDecodeError:
        return []

    if not isinstance(payload, list):
        return []
    return [item for item in payload if isinstance(item, dict)]


def _resolve_torch_dtype(device: str, compute_type: str) -> Any:
    if torch is None:
        return None

    normalized_device = (device or "auto").strip().lower()
    normalized_compute = (compute_type or "default").strip().lower()

    if normalized_device == "cpu":
        return torch.float32

    if normalized_compute in {"int8_float16", "float16"}:
        return torch.float16
    if normalized_compute == "bfloat16":
        return torch.bfloat16
    return torch.float16 if normalized_device in {"auto", "cuda"} else torch.float32


def _write_pcm16_wav(path: Path, pcm_bytes: bytes, sample_rate: int = 16_000) -> None:
    with wave.open(str(path), "wb") as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(2)
        wav_file.setframerate(sample_rate)
        wav_file.writeframes(pcm_bytes)


def _safe_float(value: Any) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return 0.0


def _json_log(event: str, **fields: Any) -> str:
    payload: dict[str, Any] = {"event": event, **fields}
    return json.dumps(payload, ensure_ascii=False, separators=(",", ":"))


def _env_int(name: str, default: int, min_value: int, max_value: int) -> int:
    raw = os.getenv(name)
    if raw is None:
        return default
    try:
        value = int(raw)
    except ValueError:
        return default
    return max(min_value, min(max_value, value))


_FILLER_WORDS = {
    "um",
    "uh",
    "er",
    "ehm",
    "like",
    "you",
    "know",
    "so",
    "basically",
    "hmm",
    "ah",
    "ee",
    "mmm",
    "yani",
    "iste",
    "sey",
    "hani",
    "falan",
}
