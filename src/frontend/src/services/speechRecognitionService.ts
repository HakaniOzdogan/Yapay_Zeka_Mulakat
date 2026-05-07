const PYTHON_SERVICE_URL = import.meta.env.VITE_PYTHON_SERVICE_URL 
  || 'http://localhost:8000/api';

export class SpeechRecognitionService {
  private mediaRecorder: MediaRecorder | null = null;
  private audioChunks: Blob[] = [];
  private chunkInterval: ReturnType<typeof setInterval> | null = null;

  // Her 4 saniyede bir chunk gönderir
  private readonly CHUNK_INTERVAL_MS = 4000;

  async start(onTranscript: (text: string) => void): Promise<void> {
    const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    
    this.mediaRecorder = new MediaRecorder(stream, {
      mimeType: 'audio/webm;codecs=opus'
    });

    this.mediaRecorder.ondataavailable = (e) => {
      if (e.data.size > 0) {
        this.audioChunks.push(e.data);
      }
    };

    this.mediaRecorder.start(1000); // Her 1 saniyede chunk topla

    // Her 4 saniyede birikmiş chunk'ları gönder
    this.chunkInterval = setInterval(async () => {
      if (this.audioChunks.length === 0) return;

      const blob = new Blob(this.audioChunks, { type: 'audio/webm' });
      this.audioChunks = []; // Temizle

      const text = await this.transcribe(blob);
      if (text) onTranscript(text);
    }, this.CHUNK_INTERVAL_MS);
  }

  stop(): void {
    if (this.chunkInterval) {
      clearInterval(this.chunkInterval);
      this.chunkInterval = null;
    }
    if (this.mediaRecorder) {
      this.mediaRecorder.stop();
      this.mediaRecorder.stream.getTracks().forEach(t => t.stop());
      this.mediaRecorder = null;
    }
    this.audioChunks = [];
  }

  private async transcribe(audioBlob: Blob): Promise<string> {
    const formData = new FormData();
    formData.append('audio', audioBlob, 'chunk.webm');
    formData.append('language', 'tr');

    try {
      const response = await fetch(`${PYTHON_SERVICE_URL}/transcribe`, {
        method: 'POST',
        body: formData,
      });

      if (!response.ok) {
        console.error('STT hatası:', response.status);
        return '';
      }

      const data = await response.json();
      return data.text || '';
      
    } catch (err) {
      console.error('STT bağlantı hatası:', err);
      return '';
    }
  }
}

export async function checkSTTService(): Promise<boolean> {
  try {
    const res = await fetch(`${PYTHON_SERVICE_URL}/health`);
    const data = await res.json();
    console.log('STT Servisi:', data);
    return data.status === 'ok';
  } catch {
    return false;
  }
}
