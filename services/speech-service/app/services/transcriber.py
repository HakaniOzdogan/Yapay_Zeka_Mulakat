"""
Speech-to-text transcription using faster-whisper.
"""
import numpy as np
from typing import Optional

try:
    from faster_whisper import WhisperModel
    FASTER_WHISPER_AVAILABLE = True
except ImportError:
    FASTER_WHISPER_AVAILABLE = False
    try:
        import whisper
        WHISPER_AVAILABLE = True
    except ImportError:
        WHISPER_AVAILABLE = False


class Transcriber:
    """Transcription service using faster-whisper or whisper"""
    
    def __init__(self, model_name: str = "base", device: str = "auto"):
        if FASTER_WHISPER_AVAILABLE:
            self.model = WhisperModel(model_name, device=device)
            self.use_faster_whisper = True
        elif WHISPER_AVAILABLE:
            import whisper
            self.model = whisper.load_model(model_name, device=device)
            self.use_faster_whisper = False
        else:
            raise RuntimeError("Neither faster-whisper nor whisper is installed")
    
    def transcribe(
        self,
        audio: np.ndarray,
        sample_rate: int,
        language: Optional[str] = None
    ) -> dict:
        """
        Transcribe audio to text with segment timing.
        
        Args:
            audio: audio samples (float32, normalized)
            sample_rate: sample rate in Hz
            language: language code (e.g., "tr", "en")
            
        Returns:
            {
                "text": full transcript,
                "segments": [
                    {"id": 0, "start": 0.0, "end": 1.2, "text": "Hello"},
                    ...
                ]
            }
        """
        if self.use_faster_whisper:
            kwargs = {
                "beam_size": 5,
            }
            if language:
                kwargs["language"] = language

            # Keep compatibility with faster-whisper versions that do not
            # support extra keyword arguments like `language_confidence`.
            segments, info = self.model.transcribe(audio, **kwargs)
            result = {
                "text": "",
                "segments": []
            }
            for i, seg in enumerate(segments):
                result["segments"].append({
                    "id": i,
                    "start": seg.start,
                    "end": seg.end,
                    "text": seg.text.strip()
                })
                result["text"] += seg.text
        else:
            # Standard whisper
            result = self.model.transcribe(audio, language=language)
        
        return result


# Global transcriber instance
_transcriber: Optional[Transcriber] = None


def get_transcriber() -> Transcriber:
    """Get or initialize transcriber"""
    global _transcriber
    if _transcriber is None:
        _transcriber = Transcriber(model_name="base")
    return _transcriber
