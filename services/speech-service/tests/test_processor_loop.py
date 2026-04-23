from __future__ import annotations

import asyncio
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from app.main import InboundChunk, _processor_loop
from app.protocol import SessionConfig
from app.session import TranscriptionSession
from app.vad import VadDecision


class _StubVadClassifier:
    def __init__(self) -> None:
        self.calls = 0

    def classify_chunk(self, audio_bytes: bytes, *, chunk_ms: int, speech_threshold: float, chunk_rms: float, rolling_audio_bytes: bytes | None = None, rolling_chunk_ms: int | None = None) -> VadDecision:
        self.calls += 1
        is_voiced = self.calls <= 3
        return VadDecision(
            is_speech=is_voiced,
            speech_ms=chunk_ms if is_voiced else 0,
            backend="energy+silero",
            energy_is_speech=is_voiced,
            energy_speech_ms=chunk_ms if is_voiced else 0,
            silero_is_speech=False,
            silero_speech_ms=0,
            silero_window_ms=rolling_chunk_ms or chunk_ms,
            chunk_rms=chunk_rms,
            speech_threshold=speech_threshold,
        )


class _SilentVadClassifier:
    def classify_chunk(self, audio_bytes: bytes, *, chunk_ms: int, speech_threshold: float, chunk_rms: float, rolling_audio_bytes: bytes | None = None, rolling_chunk_ms: int | None = None) -> VadDecision:
        return VadDecision(
            is_speech=False,
            speech_ms=0,
            backend="energy",
            energy_is_speech=False,
            energy_speech_ms=0,
            silero_is_speech=False,
            silero_speech_ms=0,
            silero_window_ms=rolling_chunk_ms or chunk_ms,
            chunk_rms=chunk_rms,
            speech_threshold=speech_threshold,
        )


def test_processor_loop_queues_decode_jobs_for_voiced_chunks(monkeypatch) -> None:
    from app import main as speech_main

    stub_vad = _StubVadClassifier()
    monkeypatch.setattr(speech_main, "_vad_classifier", stub_vad)

    async def run() -> list[object]:
        session = TranscriptionSession(
            session_id="test-session",
            connection_id="conn-1",
            default_language="tr",
            default_model="tiny",
            inbound_queue=asyncio.Queue(),
            transcribe_queue=asyncio.Queue(),
        )
        session.apply_config(SessionConfig(language="tr", model="tiny", task="transcribe", use_vad=True))

        pcm_chunk = b"\x11\x00" * 4000
        for seq in range(4):
            await session.inbound_queue.put(InboundChunk(kind="audio", seq=seq, audio_bytes=pcm_chunk, chunk_ms=250))
        await session.inbound_queue.put(InboundChunk(kind="end"))

        await _processor_loop(session)

        jobs: list[object] = []
        while not session.transcribe_queue.empty():
            jobs.append(await session.transcribe_queue.get())
        return jobs

    jobs = asyncio.run(run())

    non_terminal_jobs = [job for job in jobs if job is not None]
    assert any(job.reason == "stream_interval" for job in non_terminal_jobs)
    assert any(job.reason == "client_end" for job in non_terminal_jobs)


def test_processor_loop_does_not_queue_decode_jobs_for_silence(monkeypatch) -> None:
    from app import main as speech_main

    monkeypatch.setattr(speech_main, "_vad_classifier", _SilentVadClassifier())

    async def run() -> list[object]:
        session = TranscriptionSession(
            session_id="silent-session",
            connection_id="conn-2",
            default_language="tr",
            default_model="small",
            inbound_queue=asyncio.Queue(),
            transcribe_queue=asyncio.Queue(),
        )
        session.apply_config(SessionConfig(language="tr", model="small", task="transcribe", use_vad=True))

        silent_chunk = b"\x00\x00" * 4000
        for seq in range(4):
            await session.inbound_queue.put(InboundChunk(kind="audio", seq=seq, audio_bytes=silent_chunk, chunk_ms=250))
        await session.inbound_queue.put(InboundChunk(kind="end"))

        await _processor_loop(session)

        jobs: list[object] = []
        while not session.transcribe_queue.empty():
            jobs.append(await session.transcribe_queue.get())
        return jobs

    jobs = asyncio.run(run())

    non_terminal_jobs = [job for job in jobs if job is not None]
    assert non_terminal_jobs == []
