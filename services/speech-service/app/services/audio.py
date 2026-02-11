"""
Audio processing utilities.
"""
import numpy as np
import io
import os
import subprocess
import tempfile
from typing import Tuple

try:
    import librosa
    LIBROSA_AVAILABLE = True
except ImportError:
    LIBROSA_AVAILABLE = False


def load_audio_from_bytes(audio_bytes: bytes) -> Tuple[np.ndarray, int]:
    """
    Load audio from bytes (wav or mp3).
    
    Returns:
        (audio, sample_rate)
        audio: float32 normalized in [-1, 1]
    """
    if not LIBROSA_AVAILABLE:
        raise RuntimeError("librosa not installed")
    
    if not audio_bytes:
        raise ValueError("Empty audio payload")

    # Fast path: in-memory decode (works for wav/mp3 and some containers)
    try:
        audio, sr = librosa.load(io.BytesIO(audio_bytes), sr=None, mono=True)
        return audio, sr
    except Exception:
        pass

    # Fallback 1: decode from temp file path (helps with container formats)
    tmp_path = None
    try:
        with tempfile.NamedTemporaryFile(delete=False, suffix=".audio") as tmp:
            tmp.write(audio_bytes)
            tmp_path = tmp.name

        try:
            audio, sr = librosa.load(tmp_path, sr=None, mono=True)
            return audio, sr
        except Exception:
            pass

        # Fallback 2: force ffmpeg conversion to PCM WAV via stdout pipe
        cmd = [
            "ffmpeg",
            "-nostdin",
            "-hide_banner",
            "-loglevel",
            "error",
            "-i",
            tmp_path,
            "-f",
            "wav",
            "-ac",
            "1",
            "-ar",
            "16000",
            "pipe:1",
        ]
        result = subprocess.run(
            cmd,
            check=True,
            capture_output=True,
        )
        audio, sr = librosa.load(io.BytesIO(result.stdout), sr=None, mono=True)
        return audio, sr
    finally:
        if tmp_path and os.path.exists(tmp_path):
            os.remove(tmp_path)


def normalize_audio(audio: np.ndarray) -> np.ndarray:
    """Normalize audio to [-1, 1] float32"""
    if audio.dtype.kind == 'i':
        # Integer audio
        info = np.iinfo(audio.dtype)
        audio = audio.astype(np.float32) / max(abs(info.min), abs(info.max))
    return audio.astype(np.float32)


def compute_rms(audio: np.ndarray, frame_size: int = 2048) -> np.ndarray:
    """Compute RMS energy per frame"""
    rms = []
    for i in range(0, len(audio), frame_size):
        frame = audio[i:i + frame_size]
        rms.append(np.sqrt(np.mean(frame ** 2)))
    return np.array(rms)


def detect_pauses(
    audio: np.ndarray,
    sample_rate: int,
    threshold_db: float = -40,
    min_pause_duration_ms: int = 100
) -> Tuple[int, float]:
    """
    Detect pauses in audio.
    
    Returns:
        (pause_count, average_pause_ms)
    """
    frame_size = int(sample_rate * 20 / 1000)  # 20ms frames
    frames = []
    
    for i in range(0, len(audio), frame_size):
        frame = audio[i:i + frame_size]
        if len(frame) < frame_size:
            frame = np.pad(frame, (0, frame_size - len(frame)))
        
        rms = np.sqrt(np.mean(frame ** 2))
        db = 20 * np.log10(max(rms, 1e-10))
        is_voice = db > threshold_db
        frames.append(is_voice)
    
    # Find pause intervals
    frame_duration_ms = 20
    pauses = []
    in_pause = False
    pause_start_frame = 0
    
    for i, is_voice in enumerate(frames):
        if not is_voice and not in_pause:
            pause_start_frame = i
            in_pause = True
        elif is_voice and in_pause:
            pause_duration_ms = (i - pause_start_frame) * frame_duration_ms
            if pause_duration_ms >= min_pause_duration_ms:
                pauses.append(pause_duration_ms)
            in_pause = False
    
    if in_pause:
        pause_duration_ms = (len(frames) - pause_start_frame) * frame_duration_ms
        if pause_duration_ms >= min_pause_duration_ms:
            pauses.append(pause_duration_ms)
    
    pause_count = len(pauses)
    average_pause_ms = sum(pauses) / len(pauses) if pauses else 0.0
    
    return pause_count, average_pause_ms
