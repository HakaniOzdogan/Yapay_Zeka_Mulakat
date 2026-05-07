from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware
from routers import transcription

app = FastAPI(
    title="Yapay Zeka Mülakat — STT Servisi",
    description="Faster-Whisper tabanlı Speech-to-Text API",
    version="1.0.0"
)

# Next.js'in CORS politikasına izin ver
app.add_middleware(
    CORSMiddleware,
    allow_origins=["http://localhost:3000"],  # Next.js geliştirme portu
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

app.include_router(transcription.router)

if __name__ == "__main__":
    import uvicorn
    uvicorn.run("main:app", host="0.0.0.0", port=8000, reload=False)
