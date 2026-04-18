#!/usr/bin/env python3
import argparse
import asyncio
import base64
import csv
import json
import math
import random
import struct
import time
import uuid
import wave
from collections import Counter
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import websockets
from websockets.exceptions import ConnectionClosed

SAMPLE_RATE = 16_000
PCM_MAX = 32767

SCENARIOS = {
    "smoke": {"clients": 2, "duration_sec": 10},
    "baseline": {"clients": 5, "duration_sec": 30},
    "soak": {"clients": 10, "duration_sec": 120},
}


@dataclass
class ClientMetrics:
    client_id: int
    connected: bool = False
    connection_error: str | None = None
    disconnected_cleanly: bool = False
    chunks_sent: int = 0
    partial_messages: int = 0
    final_messages: int = 0
    first_partial_ms: float | None = None
    first_final_ms: float | None = None
    mean_partial_local_latency_ms: float | None = None
    error: str | None = None
    close_code: int | None = None


@dataclass
class RunMetrics:
    started_at_utc: str
    scenario: str
    base_url: str
    ws_url: str
    lang: str
    total_clients: int
    duration_sec: int
    chunk_ms: int
    connect_timeout_sec: float
    response_timeout_sec: float
    no_end: bool
    wav_path: str | None
    total_chunks_sent: int = 0
    partial_messages_received: int = 0
    final_messages_received: int = 0
    successful_connections: int = 0
    failed_connections: int = 0
    connect_success_rate: float = 0.0
    disconnected_or_error_count: int = 0
    disconnect_error_rate: float = 0.0
    run_duration_sec: float = 0.0
    chunks_per_second: float = 0.0
    time_to_first_partial_ms: list[float] = field(default_factory=list)
    time_to_first_final_ms: list[float] = field(default_factory=list)
    per_message_partial_local_latency_ms: list[float] = field(default_factory=list)
    errors_by_type: dict[str, int] = field(default_factory=dict)
    clients: list[ClientMetrics] = field(default_factory=list)


def percentile(values: list[float], q: float) -> float | None:
    if not values:
        return None
    if len(values) == 1:
        return round(values[0], 2)

    sorted_values = sorted(values)
    idx = (len(sorted_values) - 1) * q
    lower = math.floor(idx)
    upper = math.ceil(idx)
    if lower == upper:
        return round(sorted_values[lower], 2)

    frac = idx - lower
    value = sorted_values[lower] + ((sorted_values[upper] - sorted_values[lower]) * frac)
    return round(value, 2)


def resolve_artifacts_dir() -> Path:
    current = Path.cwd()
    for candidate in [current, *current.parents]:
        if (candidate / ".git").exists():
            path = candidate / "artifacts" / "perf"
            path.mkdir(parents=True, exist_ok=True)
            return path

    fallback = current / "artifacts" / "perf"
    fallback.mkdir(parents=True, exist_ok=True)
    return fallback


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


def generate_chunk_bytes(seq: int, chunk_ms: int) -> bytes:
    samples = int(SAMPLE_RATE * (chunk_ms / 1000.0))
    mode = seq % 6

    if mode in (0, 1):
        return b"\x00\x00" * samples

    freq = 180.0 if mode in (2, 3) else 260.0
    amp = 0.18 if mode in (2, 4) else 0.26

    frames = bytearray(samples * 2)
    for i in range(samples):
        t = i / SAMPLE_RATE
        val = math.sin((2.0 * math.pi * freq * t) + (seq * 0.04))
        pcm = int(max(-1.0, min(1.0, val * amp)) * PCM_MAX)
        struct.pack_into("<h", frames, i * 2, pcm)

    return bytes(frames)


def load_wav_pcm16_mono_16k(path: Path) -> bytes:
    with wave.open(str(path), "rb") as wav:
        channels = wav.getnchannels()
        sample_width = wav.getsampwidth()
        sample_rate = wav.getframerate()
        frame_count = wav.getnframes()
        frames = wav.readframes(frame_count)

    if channels != 1 or sample_width != 2 or sample_rate != SAMPLE_RATE:
        raise ValueError("WAV must be PCM16 mono 16kHz.")

    return frames


def chunk_message_from_bytes(seq: int, pcm_bytes: bytes) -> dict[str, Any]:
    data_b64 = base64.b64encode(pcm_bytes).decode("ascii")
    return {"type": "audio", "seq": seq, "data_b64": data_b64}


def build_wav_chunks(wav_pcm_bytes: bytes, chunk_ms: int) -> list[bytes]:
    bytes_per_sample = 2
    samples_per_chunk = int(SAMPLE_RATE * (chunk_ms / 1000.0))
    bytes_per_chunk = max(2, samples_per_chunk * bytes_per_sample)

    chunks: list[bytes] = []
    cursor = 0
    total = len(wav_pcm_bytes)

    while cursor < total:
        end = min(total, cursor + bytes_per_chunk)
        chunk = wav_pcm_bytes[cursor:end]
        if len(chunk) < bytes_per_chunk:
            chunk = chunk + (b"\x00" * (bytes_per_chunk - len(chunk)))
        chunks.append(chunk)
        cursor = end

    if not chunks:
        chunks.append(b"\x00\x00" * samples_per_chunk)

    return chunks


async def run_client(
    client_id: int,
    ws_url: str,
    duration_sec: int,
    chunk_ms: int,
    connect_timeout_sec: float,
    response_timeout_sec: float,
    no_end: bool,
    wav_chunks: list[bytes] | None,
) -> ClientMetrics:
    metrics = ClientMetrics(client_id=client_id)
    start_ns = time.monotonic_ns()
    sent_seq_to_time_ns: dict[int, int] = {}
    partial_local_latencies: list[float] = []

    try:
        ws = await asyncio.wait_for(websockets.connect(ws_url, max_size=2**24), timeout=connect_timeout_sec)
    except Exception as exc:
        metrics.connection_error = type(exc).__name__
        return metrics

    metrics.connected = True

    async def sender() -> None:
        seq = 0
        deadline = time.monotonic() + duration_sec
        while time.monotonic() < deadline:
            if wav_chunks:
                pcm_chunk = wav_chunks[seq % len(wav_chunks)]
            else:
                pcm_chunk = generate_chunk_bytes(seq, chunk_ms)

            message = chunk_message_from_bytes(seq, pcm_chunk)
            sent_seq_to_time_ns[seq] = time.monotonic_ns()
            await ws.send(json.dumps(message))
            metrics.chunks_sent += 1
            seq += 1
            await asyncio.sleep(chunk_ms / 1000.0)

        if not no_end:
            await ws.send(json.dumps({"type": "end"}))

    async def receiver() -> None:
        while True:
            raw = await asyncio.wait_for(ws.recv(), timeout=response_timeout_sec)
            now_ns = time.monotonic_ns()

            try:
                payload = json.loads(raw)
            except json.JSONDecodeError:
                continue

            msg_type = payload.get("type")
            if msg_type == "partial":
                metrics.partial_messages += 1
                if metrics.first_partial_ms is None:
                    metrics.first_partial_ms = round((now_ns - start_ns) / 1_000_000, 2)

                t_ms = payload.get("t_ms")
                if isinstance(t_ms, int) and t_ms >= 0:
                    estimated_seq = int(t_ms // max(1, chunk_ms))
                    if estimated_seq in sent_seq_to_time_ns:
                        local_latency = (now_ns - sent_seq_to_time_ns[estimated_seq]) / 1_000_000.0
                        if local_latency >= 0:
                            partial_local_latencies.append(local_latency)

            elif msg_type == "final":
                metrics.final_messages += 1
                if metrics.first_final_ms is None:
                    metrics.first_final_ms = round((now_ns - start_ns) / 1_000_000, 2)

                if not no_end:
                    return

    try:
        sender_task = asyncio.create_task(sender())
        receiver_task = asyncio.create_task(receiver())

        await sender_task

        if no_end:
            await asyncio.sleep(min(3.0, response_timeout_sec))
            receiver_task.cancel()
        else:
            await asyncio.wait_for(receiver_task, timeout=response_timeout_sec)

        metrics.disconnected_cleanly = True
    except asyncio.TimeoutError:
        metrics.error = "ResponseTimeout"
    except ConnectionClosed as exc:
        metrics.close_code = exc.code
        metrics.error = f"ConnectionClosed:{exc.code}"
    except Exception as exc:
        metrics.error = type(exc).__name__
    finally:
        try:
            await ws.close()
        except Exception:
            pass

    if partial_local_latencies:
        metrics.mean_partial_local_latency_ms = round(sum(partial_local_latencies) / len(partial_local_latencies), 2)

    return metrics


def aggregate(
    clients: list[ClientMetrics],
    scenario: str,
    base_url: str,
    ws_url: str,
    lang: str,
    duration_sec: int,
    chunk_ms: int,
    connect_timeout_sec: float,
    response_timeout_sec: float,
    no_end: bool,
    started_monotonic: float,
    wav_path: str | None,
) -> RunMetrics:
    run_duration = round(max(0.001, time.monotonic() - started_monotonic), 2)

    report = RunMetrics(
        started_at_utc=datetime.now(timezone.utc).isoformat(),
        scenario=scenario,
        base_url=base_url,
        ws_url=ws_url,
        lang=lang,
        total_clients=len(clients),
        duration_sec=duration_sec,
        chunk_ms=chunk_ms,
        connect_timeout_sec=connect_timeout_sec,
        response_timeout_sec=response_timeout_sec,
        no_end=no_end,
        wav_path=wav_path,
    )

    report.clients = clients
    report.total_chunks_sent = sum(c.chunks_sent for c in clients)
    report.partial_messages_received = sum(c.partial_messages for c in clients)
    report.final_messages_received = sum(c.final_messages for c in clients)
    report.successful_connections = sum(1 for c in clients if c.connected)
    report.failed_connections = report.total_clients - report.successful_connections
    report.connect_success_rate = round(report.successful_connections / report.total_clients, 4)
    report.run_duration_sec = run_duration
    report.chunks_per_second = round(report.total_chunks_sent / run_duration, 2)
    report.disconnected_or_error_count = report.total_clients - sum(1 for c in clients if c.disconnected_cleanly)
    report.disconnect_error_rate = round(report.disconnected_or_error_count / report.total_clients, 4)

    partial_times = [c.first_partial_ms for c in clients if c.first_partial_ms is not None]
    final_times = [c.first_final_ms for c in clients if c.first_final_ms is not None]
    partial_local_lat = [c.mean_partial_local_latency_ms for c in clients if c.mean_partial_local_latency_ms is not None]

    report.time_to_first_partial_ms = [float(v) for v in partial_times]
    report.time_to_first_final_ms = [float(v) for v in final_times]
    report.per_message_partial_local_latency_ms = [float(v) for v in partial_local_lat]

    errors = Counter()
    for c in clients:
        if c.connection_error:
            errors[f"connect:{c.connection_error}"] += 1
        if c.error:
            errors[c.error] += 1

    report.errors_by_type = dict(errors)
    return report


def print_summary(report: RunMetrics) -> None:
    p50_partial = percentile(report.time_to_first_partial_ms, 0.50)
    p95_partial = percentile(report.time_to_first_partial_ms, 0.95)
    p50_final = percentile(report.time_to_first_final_ms, 0.50)
    p95_final = percentile(report.time_to_first_final_ms, 0.95)
    p50_local = percentile(report.per_message_partial_local_latency_ms, 0.50)
    p95_local = percentile(report.per_message_partial_local_latency_ms, 0.95)

    print()
    print("=== SPEECH WS PERF SUMMARY ===")
    print(f"scenario: {report.scenario}")
    print(f"totalClients: {report.total_clients}")
    print(f"successfulConnections: {report.successful_connections}")
    print(f"failedConnections: {report.failed_connections}")
    print(f"connectSuccessRate: {report.connect_success_rate}")
    print(f"totalChunksSent: {report.total_chunks_sent}")
    print(f"chunksPerSecond: {report.chunks_per_second}")
    print(f"partialMessagesReceived: {report.partial_messages_received}")
    print(f"finalMessagesReceived: {report.final_messages_received}")
    print(f"p50 timeToFirstPartialMs: {p50_partial}")
    print(f"p95 timeToFirstPartialMs: {p95_partial}")
    print(f"p50 timeToFirstFinalMs: {p50_final}")
    print(f"p95 timeToFirstFinalMs: {p95_final}")
    print(f"p50 partialLocalLatencyMs: {p50_local}")
    print(f"p95 partialLocalLatencyMs: {p95_local}")
    print(f"disconnectOrErrorCount: {report.disconnected_or_error_count}")
    print(f"disconnectErrorRate: {report.disconnect_error_rate}")
    print(f"runDurationSec: {report.run_duration_sec}")
    print(f"errorsByType: {json.dumps(report.errors_by_type, ensure_ascii=False)}")
    print("==============================")
    print()


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Speech-service websocket performance benchmark")
    parser.add_argument("--base-url", default="http://localhost:8000", help="Speech-service base URL")
    parser.add_argument("--clients", type=int, default=None, help="Concurrent client count")
    parser.add_argument("--duration-sec", type=int, default=None, help="Streaming duration per client")
    parser.add_argument("--chunk-ms", type=int, default=250, help="Chunk pacing milliseconds")
    parser.add_argument("--lang", default="tr", choices=["tr", "en"], help="Language query parameter")
    parser.add_argument("--scenario", default="baseline", choices=["smoke", "baseline", "soak"], help="Preset scenario")
    parser.add_argument("--connect-timeout", type=float, default=10.0, help="WebSocket connect timeout seconds")
    parser.add_argument("--response-timeout", type=float, default=15.0, help="Max wait for server response seconds")
    parser.add_argument("--no-end", action="store_true", help="Do not send end message")
    parser.add_argument("--seed", type=int, default=42, help="Random seed for deterministic run metadata")
    parser.add_argument("--wav", default="", help="Optional path to PCM16 mono 16kHz WAV file")
    parser.add_argument("--csv", action="store_true", help="Also write per-client CSV report")
    return parser.parse_args()


def write_csv_report(path: Path, clients: list[ClientMetrics]) -> None:
    with path.open("w", encoding="utf-8", newline="") as f:
        writer = csv.writer(f)
        writer.writerow(
            [
                "clientId",
                "connected",
                "connectionError",
                "disconnectedCleanly",
                "chunksSent",
                "partialMessages",
                "finalMessages",
                "firstPartialMs",
                "firstFinalMs",
                "meanPartialLocalLatencyMs",
                "error",
                "closeCode",
            ]
        )
        for c in clients:
            writer.writerow(
                [
                    c.client_id,
                    c.connected,
                    c.connection_error,
                    c.disconnected_cleanly,
                    c.chunks_sent,
                    c.partial_messages,
                    c.final_messages,
                    c.first_partial_ms,
                    c.first_final_ms,
                    c.mean_partial_local_latency_ms,
                    c.error,
                    c.close_code,
                ]
            )


async def main() -> int:
    args = parse_args()
    random.seed(args.seed)

    preset = SCENARIOS[args.scenario]
    clients = args.clients if args.clients is not None else preset["clients"]
    duration_sec = args.duration_sec if args.duration_sec is not None else preset["duration_sec"]

    clients = max(1, clients)
    duration_sec = max(1, duration_sec)
    chunk_ms = max(20, args.chunk_ms)

    wav_chunks: list[bytes] | None = None
    wav_path_used: str | None = None
    if args.wav:
        wav_path = Path(args.wav).expanduser().resolve()
        wav_pcm = load_wav_pcm16_mono_16k(wav_path)
        wav_chunks = build_wav_chunks(wav_pcm, chunk_ms)
        wav_path_used = str(wav_path)

    session_id = str(uuid.uuid4())
    ws_url = build_ws_url(args.base_url, session_id, args.lang)

    started_monotonic = time.monotonic()

    tasks = [
        asyncio.create_task(
            run_client(
                client_id=i + 1,
                ws_url=ws_url,
                duration_sec=duration_sec,
                chunk_ms=chunk_ms,
                connect_timeout_sec=args.connect_timeout,
                response_timeout_sec=args.response_timeout,
                no_end=args.no_end,
                wav_chunks=wav_chunks,
            )
        )
        for i in range(clients)
    ]

    clients_metrics: list[ClientMetrics] = []
    for completed in asyncio.as_completed(tasks):
        try:
            result = await completed
            clients_metrics.append(result)
        except Exception as exc:
            clients_metrics.append(ClientMetrics(client_id=-1, error=f"Unhandled:{type(exc).__name__}"))

    report = aggregate(
        clients=sorted(clients_metrics, key=lambda c: c.client_id),
        scenario=args.scenario,
        base_url=normalize_base_url(args.base_url),
        ws_url=ws_url,
        lang=args.lang,
        duration_sec=duration_sec,
        chunk_ms=chunk_ms,
        connect_timeout_sec=args.connect_timeout,
        response_timeout_sec=args.response_timeout,
        no_end=args.no_end,
        started_monotonic=started_monotonic,
        wav_path=wav_path_used,
    )

    artifacts_dir = resolve_artifacts_dir()
    timestamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
    report_path = artifacts_dir / f"speech-ws-{timestamp}.json"
    csv_path = artifacts_dir / f"speech-ws-{timestamp}.csv"

    p50_partial = percentile(report.time_to_first_partial_ms, 0.50)
    p95_partial = percentile(report.time_to_first_partial_ms, 0.95)
    p50_final = percentile(report.time_to_first_final_ms, 0.50)
    p95_final = percentile(report.time_to_first_final_ms, 0.95)
    p50_local = percentile(report.per_message_partial_local_latency_ms, 0.50)
    p95_local = percentile(report.per_message_partial_local_latency_ms, 0.95)

    json_payload = {
        "totalClients": report.total_clients,
        "successfulConnections": report.successful_connections,
        "failedConnections": report.failed_connections,
        "connectSuccessRate": report.connect_success_rate,
        "totalChunksSent": report.total_chunks_sent,
        "chunksPerSecond": report.chunks_per_second,
        "partialMessagesReceived": report.partial_messages_received,
        "finalMessagesReceived": report.final_messages_received,
        "disconnectedOrErrorCount": report.disconnected_or_error_count,
        "disconnectErrorRate": report.disconnect_error_rate,
        "p50TimeToFirstPartialMs": p50_partial,
        "p95TimeToFirstPartialMs": p95_partial,
        "p50TimeToFirstFinalMs": p50_final,
        "p95TimeToFirstFinalMs": p95_final,
        "p50PartialLocalLatencyMs": p50_local,
        "p95PartialLocalLatencyMs": p95_local,
        "runDurationSec": report.run_duration_sec,
        "errorsByType": report.errors_by_type,
        "meta": {
            "startedAtUtc": report.started_at_utc,
            "scenario": report.scenario,
            "baseUrl": report.base_url,
            "wsUrl": report.ws_url,
            "lang": report.lang,
            "durationSec": report.duration_sec,
            "chunkMs": report.chunk_ms,
            "connectTimeoutSec": report.connect_timeout_sec,
            "responseTimeoutSec": report.response_timeout_sec,
            "noEnd": report.no_end,
            "wavPath": report.wav_path,
        },
        "clients": [
            {
                "clientId": c.client_id,
                "connected": c.connected,
                "connectionError": c.connection_error,
                "disconnectedCleanly": c.disconnected_cleanly,
                "chunksSent": c.chunks_sent,
                "partialMessages": c.partial_messages,
                "finalMessages": c.final_messages,
                "firstPartialMs": c.first_partial_ms,
                "firstFinalMs": c.first_final_ms,
                "meanPartialLocalLatencyMs": c.mean_partial_local_latency_ms,
                "error": c.error,
                "closeCode": c.close_code,
            }
            for c in report.clients
        ],
    }

    report_path.write_text(json.dumps(json_payload, ensure_ascii=False, indent=2), encoding="utf-8")
    if args.csv:
        write_csv_report(csv_path, report.clients)

    print_summary(report)
    print(f"JSON report saved: {report_path}")
    if args.csv:
        print(f"CSV report saved: {csv_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
