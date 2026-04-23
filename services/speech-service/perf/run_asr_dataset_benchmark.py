#!/usr/bin/env python3
from __future__ import annotations

import argparse
import asyncio
import base64
import csv
import json
import shutil
import subprocess
import tempfile
import time
import uuid
import wave
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any

SAMPLE_RATE = 16_000
SAMPLE_WIDTH_BYTES = 2
CHANNELS = 1
DEFAULT_CHUNK_MS = 250
DEFAULT_RESPONSE_TIMEOUT_SEC = 20.0
DEFAULT_CONNECT_TIMEOUT_SEC = 10.0
REPO_ROOT = Path(__file__).resolve().parents[3]


@dataclass
class DatasetSample:
    id: str
    source: str
    category: str
    duration_band: str
    language: str
    audio_path: str
    reference_text: str
    expected_terms: list[str]
    notes: str = ""


@dataclass
class SampleRunResult:
    sample_id: str
    model: str
    language: str
    source: str
    category: str
    duration_band: str
    audio_path: str
    transcript_text: str
    reference_text: str
    expected_terms: str
    matched_terms: str
    missing_terms: str
    term_hit_rate: float
    word_error_rate: float | None
    char_error_rate: float | None
    suggested_verdict: str
    first_partial_ms: float | None
    first_final_ms: float | None
    total_runtime_ms: float
    partial_messages: int
    final_messages: int
    ready_received: bool
    gpu_memory_used_mb: int | None
    close_code: int | None
    error: str | None


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Dataset benchmark for speech-service websocket ASR using multiple Whisper model sizes."
    )
    parser.add_argument(
        "--manifest",
        default="services/speech-service/perf/datasets/asr_benchmark_manifest.example.json",
        help="Path to dataset manifest JSON.",
    )
    parser.add_argument(
        "--base-url",
        default="http://localhost:8000",
        help="Speech-service base URL.",
    )
    parser.add_argument(
        "--models",
        default="tiny,small",
        help="Comma-separated model list to benchmark, e.g. tiny,small,medium.",
    )
    parser.add_argument(
        "--chunk-ms",
        type=int,
        default=DEFAULT_CHUNK_MS,
        help="Chunk size in milliseconds for websocket streaming.",
    )
    parser.add_argument(
        "--no-vad",
        action="store_true",
        help="Disable server-side VAD in websocket config.",
    )
    parser.add_argument(
        "--sample-id",
        action="append",
        default=[],
        help="Optional sample id filter. Can be specified multiple times.",
    )
    parser.add_argument(
        "--connect-timeout-sec",
        type=float,
        default=DEFAULT_CONNECT_TIMEOUT_SEC,
        help="Websocket connect timeout.",
    )
    parser.add_argument(
        "--response-timeout-sec",
        type=float,
        default=DEFAULT_RESPONSE_TIMEOUT_SEC,
        help="How long to wait for messages before considering the run complete after send/end.",
    )
    parser.add_argument(
        "--artifacts-dir",
        default="services/speech-service/artifacts/benchmark",
        help="Where benchmark JSON/CSV output should be written.",
    )
    return parser.parse_args()


def normalize_base_url(base_url: str) -> str:
    return base_url.rstrip("/")


def build_ws_url(base_url: str, session_id: str, lang: str) -> str:
    normalized = normalize_base_url(base_url)
    if normalized.startswith("https://"):
        ws_root = "wss://" + normalized[len("https://"):]
    elif normalized.startswith("http://"):
        ws_root = "ws://" + normalized[len("http://"):]
    elif normalized.startswith("ws://") or normalized.startswith("wss://"):
        ws_root = normalized
    else:
        ws_root = f"ws://{normalized}"
    return f"{ws_root}/ws/transcribe?session_id={session_id}&lang={lang}"


def load_manifest(path: Path) -> list[DatasetSample]:
    payload = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(payload, dict) or not isinstance(payload.get("samples"), list):
        raise ValueError("Manifest must be an object with a 'samples' array.")

    samples: list[DatasetSample] = []
    for item in payload["samples"]:
        samples.append(
            DatasetSample(
                id=str(item["id"]),
                source=str(item["source"]),
                category=str(item["category"]),
                duration_band=str(item["duration_band"]),
                language=str(item.get("language", "tr")),
                audio_path=str(item["audio_path"]),
                reference_text=str(item.get("reference_text", "")),
                expected_terms=[str(term) for term in item.get("expected_terms", [])],
                notes=str(item.get("notes", "")),
            )
        )
    return samples


def resolve_audio_path(raw_path: str) -> Path:
    candidate = Path(raw_path)
    if candidate.is_absolute():
        return candidate

    cwd_candidate = Path.cwd() / candidate
    if cwd_candidate.exists():
        return cwd_candidate

    repo_candidate = REPO_ROOT / candidate
    return repo_candidate


def prepare_audio_file(path: Path) -> Path:
    if not path.exists():
        raise FileNotFoundError(f"Audio file not found: {path}")

    try:
        with wave.open(str(path), "rb") as wav_file:
            if (
                wav_file.getnchannels() == CHANNELS
                and wav_file.getsampwidth() == SAMPLE_WIDTH_BYTES
                and wav_file.getframerate() == SAMPLE_RATE
            ):
                return path
    except wave.Error:
        pass

    ffmpeg = shutil.which("ffmpeg")
    if ffmpeg is None:
        raise RuntimeError(
            f"Audio file requires conversion but ffmpeg is not available on PATH: {path}"
        )

    temp_dir = Path(tempfile.mkdtemp(prefix="speech-benchmark-"))
    converted_path = temp_dir / f"{path.stem}-16k-mono.wav"
    subprocess.run(
        [
            ffmpeg,
            "-y",
            "-i",
            str(path),
            "-ac",
            "1",
            "-ar",
            str(SAMPLE_RATE),
            "-sample_fmt",
            "s16",
            str(converted_path),
        ],
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    return converted_path


def load_pcm_chunks(path: Path, chunk_ms: int) -> list[bytes]:
    with wave.open(str(path), "rb") as wav_file:
        channels = wav_file.getnchannels()
        sample_width = wav_file.getsampwidth()
        sample_rate = wav_file.getframerate()
        frame_count = wav_file.getnframes()
        frames = wav_file.readframes(frame_count)

    if channels != CHANNELS or sample_width != SAMPLE_WIDTH_BYTES or sample_rate != SAMPLE_RATE:
        raise ValueError("Prepared WAV must be PCM16 mono 16kHz.")

    bytes_per_chunk = int(SAMPLE_RATE * (chunk_ms / 1000.0)) * SAMPLE_WIDTH_BYTES
    if bytes_per_chunk <= 0:
        raise ValueError("chunk_ms must be positive.")

    chunks: list[bytes] = []
    cursor = 0
    while cursor < len(frames):
        chunk = frames[cursor : cursor + bytes_per_chunk]
        if len(chunk) < bytes_per_chunk:
            chunk += b"\x00" * (bytes_per_chunk - len(chunk))
        chunks.append(chunk)
        cursor += bytes_per_chunk

    if not chunks:
        chunks.append(b"\x00" * bytes_per_chunk)

    return chunks


def _normalize_text(text: str) -> str:
    cleaned = "".join(ch.lower() if ch.isalnum() or ch.isspace() else " " for ch in text)
    return " ".join(cleaned.split())


def _tokenize(text: str) -> list[str]:
    normalized = _normalize_text(text)
    return normalized.split() if normalized else []


def _levenshtein(left: list[str], right: list[str]) -> int:
    if not left:
        return len(right)
    if not right:
        return len(left)

    prev = list(range(len(right) + 1))
    for i, left_item in enumerate(left, start=1):
        current = [i]
        for j, right_item in enumerate(right, start=1):
            cost = 0 if left_item == right_item else 1
            current.append(
                min(
                    prev[j] + 1,
                    current[j - 1] + 1,
                    prev[j - 1] + cost,
                )
            )
        prev = current
    return prev[-1]


def compute_word_error_rate(reference_text: str, hypothesis_text: str) -> float | None:
    reference_tokens = _tokenize(reference_text)
    hypothesis_tokens = _tokenize(hypothesis_text)
    if not reference_tokens:
        return None
    distance = _levenshtein(reference_tokens, hypothesis_tokens)
    return round(distance / len(reference_tokens), 4)


def compute_char_error_rate(reference_text: str, hypothesis_text: str) -> float | None:
    reference_chars = list(_normalize_text(reference_text).replace(" ", ""))
    hypothesis_chars = list(_normalize_text(hypothesis_text).replace(" ", ""))
    if not reference_chars:
        return None
    distance = _levenshtein(reference_chars, hypothesis_chars)
    return round(distance / len(reference_chars), 4)


def compute_term_hits(expected_terms: list[str], transcript_text: str) -> tuple[list[str], list[str], float]:
    normalized_transcript = _normalize_text(transcript_text)
    matched: list[str] = []
    missing: list[str] = []
    for term in expected_terms:
        normalized_term = _normalize_text(term)
        if normalized_term and normalized_term in normalized_transcript:
            matched.append(term)
        else:
            missing.append(term)

    hit_rate = 1.0 if not expected_terms else round(len(matched) / len(expected_terms), 4)
    return matched, missing, hit_rate


def suggest_verdict(wer: float | None, term_hit_rate: float) -> str:
    if wer is None:
        return "reference_missing"
    if wer <= 0.2 and term_hit_rate >= 0.8:
        return "dogru"
    if wer <= 0.45 and term_hit_rate >= 0.5:
        return "kismen_dogru"
    return "anlamsiz_veya_konu_disi"


def get_gpu_memory_used_mb() -> int | None:
    tool = shutil.which("nvidia-smi")
    if tool is None:
        return None
    try:
        result = subprocess.run(
            [tool, "--query-gpu=memory.used", "--format=csv,noheader,nounits"],
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
    except Exception:
        return None

    first_line = next((line.strip() for line in result.stdout.splitlines() if line.strip()), "")
    try:
        return int(first_line)
    except ValueError:
        return None


async def run_sample(
    *,
    sample: DatasetSample,
    model: str,
    base_url: str,
    chunk_ms: int,
    connect_timeout_sec: float,
    response_timeout_sec: float,
    use_vad: bool,
) -> SampleRunResult:
    import websockets

    source_audio_path = resolve_audio_path(sample.audio_path)
    prepared_audio = prepare_audio_file(source_audio_path)
    chunks = load_pcm_chunks(prepared_audio, chunk_ms)
    ws_url = build_ws_url(base_url, str(uuid.uuid4()), sample.language)
    start_ns = time.monotonic_ns()
    transcript_lines: list[str] = []
    interim_text = ""
    first_partial_ms: float | None = None
    first_final_ms: float | None = None
    partial_messages = 0
    final_messages = 0
    ready_received = False
    close_code: int | None = None
    run_error: str | None = None

    gpu_before = get_gpu_memory_used_mb()
    gpu_after: int | None = None

    try:
        websocket = await asyncio.wait_for(
            websockets.connect(ws_url, max_size=2**24),
            timeout=connect_timeout_sec,
        )
    except Exception as exc:
        return SampleRunResult(
            sample_id=sample.id,
            model=model,
            language=sample.language,
            source=sample.source,
            category=sample.category,
            duration_band=sample.duration_band,
            audio_path=sample.audio_path,
            transcript_text="",
            reference_text=sample.reference_text,
            expected_terms=" | ".join(sample.expected_terms),
            matched_terms="",
            missing_terms=" | ".join(sample.expected_terms),
            term_hit_rate=0.0,
            word_error_rate=None,
            char_error_rate=None,
            suggested_verdict="connection_failed",
            first_partial_ms=None,
            first_final_ms=None,
            total_runtime_ms=round((time.monotonic_ns() - start_ns) / 1_000_000, 2),
            partial_messages=0,
            final_messages=0,
            ready_received=False,
            gpu_memory_used_mb=gpu_before,
            close_code=None,
            error=type(exc).__name__,
        )

    async with websocket:
        await websocket.send(
            json.dumps(
                {
                    "type": "config",
                    "language": sample.language,
                    "model": model,
                    "task": "transcribe",
                    "use_vad": use_vad,
                }
            )
        )

        async def receiver() -> None:
            nonlocal ready_received
            nonlocal first_partial_ms
            nonlocal first_final_ms
            nonlocal partial_messages
            nonlocal final_messages
            nonlocal interim_text
            nonlocal run_error
            nonlocal close_code

            while True:
                raw = await asyncio.wait_for(websocket.recv(), timeout=response_timeout_sec)
                now_ms = round((time.monotonic_ns() - start_ns) / 1_000_000, 2)
                payload = json.loads(raw)
                msg_type = payload.get("type")

                if msg_type == "ready":
                    ready_received = True
                    continue

                if msg_type == "partial":
                    partial_messages += 1
                    interim_text = str(payload.get("text") or "").strip()
                    if first_partial_ms is None:
                        first_partial_ms = now_ms
                    continue

                if msg_type == "final":
                    final_messages += 1
                    if first_final_ms is None:
                        first_final_ms = now_ms
                    segments = payload.get("segments") or []
                    for segment in segments:
                        text = str(segment.get("text") or "").strip()
                        if text:
                            transcript_lines.append(text)
                    interim_text = ""
                    continue

                if msg_type == "error":
                    run_error = str(payload.get("detail") or payload.get("error") or "ws_error")
                    return

        async def sender() -> None:
            for chunk in chunks:
                await websocket.send(
                    json.dumps(
                        {
                            "type": "audio",
                            "seq": sender.seq,
                            "data_b64": base64.b64encode(chunk).decode("ascii"),
                        }
                    )
                )
                sender.seq += 1
                await asyncio.sleep(chunk_ms / 1000.0)
            await websocket.send(json.dumps({"type": "end"}))

        sender.seq = 0  # type: ignore[attr-defined]

        receiver_task = asyncio.create_task(receiver())
        sender_task = asyncio.create_task(sender())

        try:
            await sender_task
            await asyncio.wait_for(receiver_task, timeout=response_timeout_sec)
        except asyncio.TimeoutError:
            receiver_task.cancel()
        except websockets.exceptions.ConnectionClosed as exc:
            close_code = exc.code
        finally:
            if not receiver_task.done():
                receiver_task.cancel()
                await asyncio.gather(receiver_task, return_exceptions=True)

    gpu_after = get_gpu_memory_used_mb()
    gpu_peak = max(value for value in [gpu_before, gpu_after] if value is not None) if (gpu_before is not None or gpu_after is not None) else None

    transcript_text = " ".join(part for part in [*transcript_lines, interim_text] if part).strip()
    wer = compute_word_error_rate(sample.reference_text, transcript_text)
    cer = compute_char_error_rate(sample.reference_text, transcript_text)
    matched_terms, missing_terms, term_hit_rate = compute_term_hits(sample.expected_terms, transcript_text)

    return SampleRunResult(
        sample_id=sample.id,
        model=model,
        language=sample.language,
        source=sample.source,
        category=sample.category,
        duration_band=sample.duration_band,
        audio_path=sample.audio_path,
        transcript_text=transcript_text,
        reference_text=sample.reference_text,
        expected_terms=" | ".join(sample.expected_terms),
        matched_terms=" | ".join(matched_terms),
        missing_terms=" | ".join(missing_terms),
        term_hit_rate=term_hit_rate,
        word_error_rate=wer,
        char_error_rate=cer,
        suggested_verdict=suggest_verdict(wer, term_hit_rate),
        first_partial_ms=first_partial_ms,
        first_final_ms=first_final_ms,
        total_runtime_ms=round((time.monotonic_ns() - start_ns) / 1_000_000, 2),
        partial_messages=partial_messages,
        final_messages=final_messages,
        ready_received=ready_received,
        gpu_memory_used_mb=gpu_peak,
        close_code=close_code,
        error=run_error,
    )


def write_csv(path: Path, rows: list[SampleRunResult]) -> None:
    if not rows:
        return
    fieldnames = list(asdict(rows[0]).keys())
    with path.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        for row in rows:
            writer.writerow(asdict(row))


def aggregate_summary(rows: list[SampleRunResult]) -> dict[str, Any]:
    summary_by_model: dict[str, dict[str, Any]] = {}
    for row in rows:
        model_bucket = summary_by_model.setdefault(
            row.model,
            {
                "samples": 0,
                "mean_wer": [],
                "mean_cer": [],
                "mean_term_hit_rate": [],
                "mean_first_partial_ms": [],
                "mean_first_final_ms": [],
                "verdicts": {},
                "errors": 0,
            },
        )
        model_bucket["samples"] += 1
        if row.word_error_rate is not None:
            model_bucket["mean_wer"].append(row.word_error_rate)
        if row.char_error_rate is not None:
            model_bucket["mean_cer"].append(row.char_error_rate)
        model_bucket["mean_term_hit_rate"].append(row.term_hit_rate)
        if row.first_partial_ms is not None:
            model_bucket["mean_first_partial_ms"].append(row.first_partial_ms)
        if row.first_final_ms is not None:
            model_bucket["mean_first_final_ms"].append(row.first_final_ms)
        model_bucket["verdicts"][row.suggested_verdict] = model_bucket["verdicts"].get(row.suggested_verdict, 0) + 1
        if row.error:
            model_bucket["errors"] += 1

    for model_bucket in summary_by_model.values():
        for key in ["mean_wer", "mean_cer", "mean_term_hit_rate", "mean_first_partial_ms", "mean_first_final_ms"]:
            values = model_bucket[key]
            model_bucket[key] = round(sum(values) / len(values), 4) if values else None

    return summary_by_model


async def main() -> int:
    args = parse_args()

    try:
        import websockets  # noqa: F401
    except ModuleNotFoundError as exc:
        raise SystemExit(
            "Missing dependency: websockets. Install services/speech-service/perf/requirements.txt first."
        ) from exc

    manifest_path = Path(args.manifest)
    artifacts_dir = Path(args.artifacts_dir)
    artifacts_dir.mkdir(parents=True, exist_ok=True)

    samples = load_manifest(manifest_path)
    if args.sample_id:
        sample_ids = set(args.sample_id)
        samples = [sample for sample in samples if sample.id in sample_ids]

    if not samples:
        raise SystemExit("No samples selected from manifest.")

    models = [model.strip() for model in args.models.split(",") if model.strip()]
    if not models:
        raise SystemExit("No models specified.")

    results: list[SampleRunResult] = []
    for model in models:
        for sample in samples:
            print(f"[benchmark] model={model} sample={sample.id} category={sample.category}")
            result = await run_sample(
                sample=sample,
                model=model,
                base_url=args.base_url,
                chunk_ms=args.chunk_ms,
                connect_timeout_sec=args.connect_timeout_sec,
                response_timeout_sec=args.response_timeout_sec,
                use_vad=not args.no_vad,
            )
            results.append(result)

    timestamp = time.strftime("%Y%m%d-%H%M%S")
    csv_path = artifacts_dir / f"asr-benchmark-{timestamp}.csv"
    json_path = artifacts_dir / f"asr-benchmark-{timestamp}.json"
    write_csv(csv_path, results)
    payload = {
        "manifest": str(manifest_path),
        "baseUrl": normalize_base_url(args.base_url),
        "models": models,
        "chunkMs": args.chunk_ms,
        "useVad": not args.no_vad,
        "summaryByModel": aggregate_summary(results),
        "results": [asdict(item) for item in results],
    }
    json_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")

    print(f"[benchmark] CSV report: {csv_path}")
    print(f"[benchmark] JSON report: {json_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
