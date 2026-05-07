from fastapi import APIRouter, UploadFile, File, Query, HTTPException
from faster_whisper import WhisperModel
import tempfile
import os
import logging

logger = logging.getLogger(__name__)
router = APIRouter(prefix="/api", tags=["transcription"])

# ─────────────────────────────────────────────
# Model bir kez yüklenir, her istek yeniden yüklemez
# ─────────────────────────────────────────────
try:
    model = WhisperModel(
        "medium",
        device="cuda",           # GPU kullan
        compute_type="float16",  # RTX 3050 Ti için optimize (4 GB VRAM)
        download_root="./models" # Modeli proje içinde sakla
    )
    logger.info("✅ Faster-Whisper medium modeli GPU'ya yüklendi.")
except Exception as e:
    logger.warning(f"⚠️ GPU yüklenemedi, CPU'ya geçiliyor: {e}")
    model = WhisperModel(
        "medium",
        device="cpu",
        compute_type="int8",     # CPU için int8 daha hızlı
        download_root="./models"
    )
    logger.info("✅ Faster-Whisper medium modeli CPU'ya yüklendi.")


@router.post("/transcribe")
async def transcribe(
    audio: UploadFile = File(...),
    language: str = Query(default="tr", description="Dil kodu (tr, en, de...)")
):
    """
    Ses dosyasını metne çevirir.
    
    - **audio**: WebM, WAV, MP3, OGG formatlarını destekler
    - **language**: ISO 639-1 dil kodu (varsayılan: tr)
    """
    
    # Dosya uzantısını al
    original_name = audio.filename or "audio.webm"
    suffix = os.path.splitext(original_name)[1] or ".webm"
    
    # Geçici dosyaya yaz
    with tempfile.NamedTemporaryFile(delete=False, suffix=suffix) as tmp:
        content = await audio.read()
        
        if len(content) == 0:
            raise HTTPException(status_code=400, detail="Ses dosyası boş.")
        
        tmp.write(content)
        tmp_path = tmp.name

    try:
        segments, info = model.transcribe(
            tmp_path,
            language=language,
            beam_size=5,                    # Doğruluk/hız dengesi (1-10)
            best_of=5,
            temperature=0.0,               # Tutarlı sonuçlar için
            condition_on_previous_text=True,
            vad_filter=True,               # Sessiz kısımları otomatik atla
            vad_parameters=dict(
                min_silence_duration_ms=500,   # 500ms sessizlik = segment sonu
                speech_pad_ms=200              # Konuşmadan önce/sonra 200ms tampon
            )
        )
        
        # Segment listesini text'e dönüştür
        segments_list = list(segments)  # generator'ı tüket
        text = " ".join([seg.text.strip() for seg in segments_list if seg.text.strip()])
        
        return {
            "text": text,
            "language": info.language,
            "language_probability": round(info.language_probability, 3),
            "duration": round(info.duration, 2),
            "segments_count": len(segments_list)
        }
        
    except Exception as e:
        logger.error(f"Transkripsiyon hatası: {e}")
        raise HTTPException(status_code=500, detail=f"Transkripsiyon başarısız: {str(e)}")
        
    finally:
        # Geçici dosyayı her durumda sil
        if os.path.exists(tmp_path):
            os.unlink(tmp_path)


@router.get("/health")
async def health_check():
    """Servis sağlık kontrolü"""
    import torch
    return {
        "status": "ok",
        "model": "faster-whisper-medium",
        "device": "cuda" if (hasattr(model, 'model') and torch.cuda.is_available()) else "cpu",
        "gpu": torch.cuda.get_device_name(0) if torch.cuda.is_available() else None
    }
