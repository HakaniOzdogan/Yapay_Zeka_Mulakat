"""
Voice Activity Detection using energy threshold.
Fallback if webrtcvad is unavailable.
"""
import numpy as np
from typing import Tuple, List


class EnergyThresholdVAD:
    """Simple VAD using RMS energy threshold"""
    
    def __init__(self, threshold_db: float = -40, frame_duration_ms: int = 20):
        self.threshold_db = threshold_db
        self.frame_duration_ms = frame_duration_ms
    
    def detect_speech_frames(
        self, audio: np.ndarray, sample_rate: int
    ) -> Tuple[List[bool], List[Tuple[int, int]]]:
        """
        Detect speech frames and return silence/speech segments.
        
        Args:
            audio: audio samples (float32, normalized)
            sample_rate: sample rate in Hz
            
        Returns:
            (frame_labels, silence_intervals)
            frame_labels: list of bool (True = voice, False = silence)
            silence_intervals: list of (start_frame, end_frame) for silence
        """
        frame_size = int(sample_rate * self.frame_duration_ms / 1000)
        frames = []
        
        for i in range(0, len(audio), frame_size):
            frame = audio[i:i + frame_size]
            if len(frame) < frame_size:
                frame = np.pad(frame, (0, frame_size - len(frame)))
            
            rms = np.sqrt(np.mean(frame ** 2))
            # Convert to dB
            db = 20 * np.log10(max(rms, 1e-10))
            is_voice = db > self.threshold_db
            frames.append(is_voice)
        
        # Find silence intervals
        silence_intervals = []
        in_silence = False
        silence_start = 0
        
        for i, is_voice in enumerate(frames):
            if not is_voice and not in_silence:
                silence_start = i
                in_silence = True
            elif is_voice and in_silence:
                silence_intervals.append((silence_start, i))
                in_silence = False
        
        if in_silence:
            silence_intervals.append((silence_start, len(frames)))
        
        return frames, silence_intervals


try:
    import webrtcvad as vad_module
    WEBRTCVAD_AVAILABLE = True
except ImportError:
    WEBRTCVAD_AVAILABLE = False


class WebRTCVAD:
    """VAD using webrtcvad (if available)"""
    
    def __init__(self, aggressiveness: int = 2):
        if not WEBRTCVAD_AVAILABLE:
            raise RuntimeError("webrtcvad not installed. Use EnergyThresholdVAD instead.")
        self.vad = vad_module.Vad(aggressiveness)
        self.sample_rate = 16000  # webrtcvad requires 16kHz
        self.frame_duration_ms = 20
    
    def detect_speech_frames(
        self, audio: np.ndarray, sample_rate: int
    ) -> Tuple[List[bool], List[Tuple[int, int]]]:
        """Detect speech using webrtcvad"""
        # Resample if needed (webrtcvad only accepts 8/16/32kHz)
        if sample_rate != 16000:
            import librosa
            audio = librosa.resample(audio, orig_sr=sample_rate, target_sr=16000)
            sample_rate = 16000
        
        # Convert to int16
        audio_int16 = (audio * 32767).astype(np.int16)
        
        frame_size = int(sample_rate * self.frame_duration_ms / 1000)
        frames = []
        
        for i in range(0, len(audio_int16), frame_size):
            frame = audio_int16[i:i + frame_size]
            if len(frame) < frame_size:
                continue
            
            is_voice = self.vad.is_speech(frame.tobytes(), sample_rate)
            frames.append(is_voice)
        
        # Find silence intervals
        silence_intervals = []
        in_silence = False
        silence_start = 0
        
        for i, is_voice in enumerate(frames):
            if not is_voice and not in_silence:
                silence_start = i
                in_silence = True
            elif is_voice and in_silence:
                silence_intervals.append((silence_start, i))
                in_silence = False
        
        if in_silence:
            silence_intervals.append((silence_start, len(frames)))
        
        return frames, silence_intervals


def get_vad_engine():
    """Factory: return best available VAD engine"""
    if WEBRTCVAD_AVAILABLE:
        try:
            return WebRTCVAD()
        except Exception:
            pass
    return EnergyThresholdVAD()
