from pydantic import BaseModel
from typing import Optional


class TranscribeRequest(BaseModel):
    """Batch transcription request"""
    language: Optional[str] = "tr"
    compute_stats: bool = True


class TranscribeChunkRequest(BaseModel):
    """Near-realtime chunk transcription"""
    language: Optional[str] = "tr"


class TranscriptSegment(BaseModel):
    start_ms: int
    end_ms: int
    text: str


class TranscribeResponse(BaseModel):
    """Batch transcription response"""
    segments: list[TranscriptSegment]
    full_text: str
    
    # Stats (if compute_stats=True)
    duration_ms: int
    word_count: int
    wpm: Optional[float] = None
    filler_count: int
    filler_words: list[str]
    pause_count: int
    average_pause_ms: Optional[float] = None


class ChunkResponse(BaseModel):
    """Partial transcription response"""
    partial_text: str
    is_final: bool
