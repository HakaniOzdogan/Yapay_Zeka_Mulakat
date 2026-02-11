from fastapi import FastAPI, UploadFile, File, HTTPException
from fastapi.responses import JSONResponse
from fastapi.middleware.cors import CORSMiddleware
import logging
import numpy as np

from app.models.requests import TranscribeResponse, ChunkResponse, TranscriptSegment
from app.services.transcriber import get_transcriber
from app.services.audio import load_audio_from_bytes, detect_pauses, normalize_audio
from app.services.filler import detect_filler_words, compute_filler_rate

logger = logging.getLogger(__name__)

app = FastAPI(
    title="Interview Coach Speech Service",
    description="Speech-to-text + audio metrics",
    version="1.0"
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=[
        "http://localhost:5173",
        "http://127.0.0.1:5173",
    ],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.on_event("startup")
async def preload_transcriber():
    """
    Warm up transcription model on service startup so the first user request
    does not pay model download/initialization cost.
    """
    try:
        get_transcriber()
        logger.info("Transcriber model preloaded.")
    except Exception as e:
        logger.error(f"Transcriber preload failed: {e}")


@app.get("/health")
async def health():
    """Health check endpoint"""
    return {"status": "ok", "service": "speech-service"}


@app.post("/transcribe")
async def transcribe(
    file: UploadFile = File(...),
    language: str = "tr",
    compute_stats: bool = True
):
    """
    Batch transcription with optional stats.
    
    Returns:
        {
            "segments": [{"start_ms": 0, "end_ms": 1200, "text": "..."}],
            "full_text": "...",
            "duration_ms": 5000,
            "word_count": 50,
            "wpm": 600.0,
            "filler_count": 2,
            "filler_words": ["şey", "yani"],
            "pause_count": 3,
            "average_pause_ms": 150.5
        }
    """
    try:
        # Read audio file
        audio_bytes = await file.read()
        
        # Load audio
        try:
            audio, sr = load_audio_from_bytes(audio_bytes)
        except Exception as e:
            logger.error(f"Failed to load audio: {e}")
            raise HTTPException(status_code=400, detail="Invalid audio file")
        
        audio = normalize_audio(audio)
        
        # Transcribe
        transcriber = get_transcriber()
        result = transcriber.transcribe(audio, sr, language=language)
        
        # Build segments
        segments = []
        for seg in result.get("segments", []):
            segments.append(TranscriptSegment(
                start_ms=int(seg["start"] * 1000),
                end_ms=int(seg["end"] * 1000),
                text=seg["text"]
            ))
        
        full_text = result.get("text", "")
        duration_ms = int(len(audio) / sr * 1000)
        word_count = len(full_text.split())
        
        # Compute stats if requested
        stats = {
            "filler_count": 0,
            "filler_words": [],
            "pause_count": 0,
            "average_pause_ms": None,
            "wpm": None
        }
        
        if compute_stats:
            # Filler words
            filler_list = detect_filler_words(full_text, language)
            stats["filler_count"] = len(filler_list)
            stats["filler_words"] = list(set(filler_list))  # unique
            
            # Pauses
            pause_count, avg_pause_ms = detect_pauses(audio, sr)
            stats["pause_count"] = pause_count
            stats["average_pause_ms"] = avg_pause_ms
            
            # WPM
            duration_seconds = duration_ms / 1000
            if duration_seconds > 0:
                stats["wpm"] = (word_count / duration_seconds) * 60
        
        response = TranscribeResponse(
            segments=segments,
            full_text=full_text,
            duration_ms=duration_ms,
            word_count=word_count,
            wpm=stats["wpm"],
            filler_count=stats["filler_count"],
            filler_words=stats["filler_words"],
            pause_count=stats["pause_count"],
            average_pause_ms=stats["average_pause_ms"]
        )
        
        return response.model_dump()
    
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Transcription error: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(e))


@app.post("/transcribe-chunk")
async def transcribe_chunk(
    file: UploadFile = File(...),
    language: str = "tr"
):
    """
    Near-realtime chunk transcription (best-effort partial).
    Returns partial text and is_final flag.
    """
    try:
        audio_bytes = await file.read()
        
        try:
            audio, sr = load_audio_from_bytes(audio_bytes)
        except Exception as e:
            logger.error(f"Failed to load audio chunk: {e}")
            raise HTTPException(status_code=400, detail="Invalid audio file")
        
        audio = normalize_audio(audio)
        
        # Transcribe
        transcriber = get_transcriber()
        result = transcriber.transcribe(audio, sr, language=language)
        partial_text = result.get("text", "")
        
        # For now, treat each chunk as potentially final (real implementation would use
        # streaming endpoints and context from previous chunks)
        response = ChunkResponse(
            partial_text=partial_text,
            is_final=True  # Simplified; real streaming would set False
        )
        
        return response.model_dump()
    
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Chunk transcription error: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(e))
