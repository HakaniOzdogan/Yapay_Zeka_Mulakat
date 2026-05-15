# 05 — Video Kayıt Sistemi

## Genel Bakış

Mülakat sırasında kullanıcının tüm ekranı ve webcam görüntüsü aynı anda kaydedilir. İki ayrı `MediaStream` (ekran + webcam) bir `<canvas>` üzerinde birleştirilerek tek bir kompozit video stream oluşturulur. Bu stream'den iki ayrı `MediaRecorder` beslenir: biri mülakatın tamamını, diğeri her sorunun clip'ini kaydeder.

## Mimari

```
getDisplayMedia() ──→ ┐
                      ├──→ Canvas Compositor ──→ captureStream(30fps)
getUserMedia()   ──→ ┘      (requestAnimFrame)         │
                            Ekran tam boyut             ├──→ Sürekli MediaRecorder
                            Webcam PiP (sağ alt)        └──→ Clip MediaRecorder
                                                              (soru bazlı start/stop)
```

## DualStreamRecorder Sınıfı

```typescript
// src/lib/video/dual-stream-recorder.ts

export interface RecordingClip {
  questionId: string;
  videoBlob: Blob;
  audioBlob: Blob;
  startedAt: Date;
  stoppedAt: Date;
  duration: number;
}

export interface DualStreamRecorderOptions {
  pipWidth?: number;
  pipHeight?: number;
  pipPadding?: number;
  pipPosition?: 'bottom-right' | 'bottom-left' | 'top-right' | 'top-left';
  frameRate?: number;
  videoBitsPerSecond?: number;
  onTabSwitch?: (hidden: boolean) => void;
}

export class DualStreamRecorder {
  private screenStream: MediaStream | null = null;
  private cameraStream: MediaStream | null = null;
  private compositeStream: MediaStream | null = null;
  private canvas: HTMLCanvasElement | null = null;
  private ctx: CanvasRenderingContext2D | null = null;
  private screenVideo: HTMLVideoElement | null = null;
  private cameraVideo: HTMLVideoElement | null = null;
  private animationId: number | null = null;

  private continuousRecorder: MediaRecorder | null = null;
  private continuousChunks: Blob[] = [];

  private clipRecorder: MediaRecorder | null = null;
  private clipChunks: Blob[] = [];
  private currentClipMeta: { questionId: string; startedAt: Date } | null = null;

  private readonly opts: Required<DualStreamRecorderOptions>;

  constructor(options: DualStreamRecorderOptions = {}) {
    this.opts = {
      pipWidth: options.pipWidth ?? 200,
      pipHeight: options.pipHeight ?? 150,
      pipPadding: options.pipPadding ?? 16,
      pipPosition: options.pipPosition ?? 'bottom-right',
      frameRate: options.frameRate ?? 30,
      videoBitsPerSecond: options.videoBitsPerSecond ?? 2_500_000,
      onTabSwitch: options.onTabSwitch ?? (() => {}),
    };
  }

  // ──────────────────────────────────────────
  // ADIM 1: Ekran ve kamera izinlerini al
  // ──────────────────────────────────────────
  async initialize(): Promise<void> {
    // Kamera + mikrofon
    this.cameraStream = await navigator.mediaDevices.getUserMedia({
      video: { width: 640, height: 480, facingMode: 'user' },
      audio: {
        echoCancellation: true,
        noiseSuppression: true,
        sampleRate: 44100,
      },
    });

    // Tüm ekran — kullanıcıya paylaşım dialogu gösterilir
    this.screenStream = await navigator.mediaDevices.getDisplayMedia({
      video: {
        displaySurface: 'monitor',  // Tüm ekranı öner
        frameRate: this.opts.frameRate,
      },
      audio: false,  // Sistem sesini kaydetme
    });

    // Ekran paylaşımı kullanıcı tarafından durdurulursa
    this.screenStream.getVideoTracks()[0].addEventListener('ended', () => {
      this.handleScreenShareStopped();
    });

    // Canvas compositor başlat
    this.setupCanvas();

    // Sekme değişimi takibi
    document.addEventListener('visibilitychange', this.handleVisibilityChange);
  }

  // ──────────────────────────────────────────
  // ADIM 2: Canvas compositor kur
  // ──────────────────────────────────────────
  private setupCanvas(): void {
    const screenTrack = this.screenStream!.getVideoTracks()[0];
    const { width, height } = screenTrack.getSettings();

    this.canvas = document.createElement('canvas');
    this.canvas.width = width || 1920;
    this.canvas.height = height || 1080;
    this.ctx = this.canvas.getContext('2d')!;

    // Ekran stream'i için video element
    this.screenVideo = document.createElement('video');
    this.screenVideo.srcObject = this.screenStream;
    this.screenVideo.muted = true;
    this.screenVideo.play();

    // Kamera stream'i için video element (ses yok — sadece görüntü)
    this.cameraVideo = document.createElement('video');
    this.cameraVideo.srcObject = new MediaStream(
      this.cameraStream!.getVideoTracks()
    );
    this.cameraVideo.muted = true;
    this.cameraVideo.play();

    // Her frame'de canvas'ı güncelle
    this.startDrawLoop();

    // Canvas'tan kompozit stream al
    this.compositeStream = this.canvas.captureStream(this.opts.frameRate);

    // Mikrofon ses track'ini kompozit stream'e ekle
    const audioTrack = this.cameraStream!.getAudioTracks()[0];
    if (audioTrack) {
      this.compositeStream.addTrack(audioTrack);
    }
  }

  private startDrawLoop(): void {
    const draw = () => {
      if (!this.ctx || !this.canvas) return;

      const W = this.canvas.width;
      const H = this.canvas.height;

      // Ekranı tam boyut çiz
      if (this.screenVideo?.readyState >= 2) {
        this.ctx.drawImage(this.screenVideo, 0, 0, W, H);
      } else {
        this.ctx.fillStyle = '#1a1a1a';
        this.ctx.fillRect(0, 0, W, H);
      }

      // PiP konumunu hesapla
      const { pipWidth: pw, pipHeight: ph, pipPadding: pad, pipPosition } = this.opts;
      let pipX: number, pipY: number;

      switch (pipPosition) {
        case 'bottom-right': pipX = W - pw - pad; pipY = H - ph - pad; break;
        case 'bottom-left':  pipX = pad;           pipY = H - ph - pad; break;
        case 'top-right':    pipX = W - pw - pad; pipY = pad;           break;
        case 'top-left':     pipX = pad;           pipY = pad;           break;
        default:             pipX = W - pw - pad; pipY = H - ph - pad;
      }

      // PiP arka planı (yuvarlak köşe görünümü için clip path)
      if (this.cameraVideo?.readyState >= 2) {
        // Yuvarlatılmış köşe için clip
        this.ctx.save();
        this.roundedRect(this.ctx, pipX, pipY, pw, ph, 12);
        this.ctx.clip();
        this.ctx.drawImage(this.cameraVideo, pipX, pipY, pw, ph);
        this.ctx.restore();

        // PiP kenarlık
        this.ctx.save();
        this.roundedRect(this.ctx, pipX, pipY, pw, ph, 12);
        this.ctx.strokeStyle = 'rgba(255,255,255,0.6)';
        this.ctx.lineWidth = 2;
        this.ctx.stroke();
        this.ctx.restore();
      }

      this.animationId = requestAnimationFrame(draw);
    };

    this.animationId = requestAnimationFrame(draw);
  }

  private roundedRect(
    ctx: CanvasRenderingContext2D,
    x: number, y: number,
    w: number, h: number,
    r: number
  ): void {
    ctx.beginPath();
    ctx.moveTo(x + r, y);
    ctx.lineTo(x + w - r, y);
    ctx.quadraticCurveTo(x + w, y, x + w, y + r);
    ctx.lineTo(x + w, y + h - r);
    ctx.quadraticCurveTo(x + w, y + h, x + w - r, y + h);
    ctx.lineTo(x + r, y + h);
    ctx.quadraticCurveTo(x, y + h, x, y + h - r);
    ctx.lineTo(x, y + r);
    ctx.quadraticCurveTo(x, y, x + r, y);
    ctx.closePath();
  }

  // ──────────────────────────────────────────
  // ADIM 3: Sürekli kaydı başlat
  // ──────────────────────────────────────────
  startContinuousRecording(): void {
    if (!this.compositeStream) throw new Error('initialize() önce çağrılmalı');

    this.continuousChunks = [];
    this.continuousRecorder = new MediaRecorder(this.compositeStream, {
      mimeType: 'video/webm;codecs=vp9,opus',
      videoBitsPerSecond: this.opts.videoBitsPerSecond,
    });

    this.continuousRecorder.ondataavailable = (e) => {
      if (e.data.size > 0) this.continuousChunks.push(e.data);
    };

    // Her 10 saniyede bir chunk al (bellek yönetimi)
    this.continuousRecorder.start(10_000);
  }

  // ──────────────────────────────────────────
  // ADIM 4: Soru clip'lerini yönet
  // ──────────────────────────────────────────
  startQuestionClip(questionId: string): void {
    if (!this.compositeStream) throw new Error('initialize() önce çağrılmalı');

    this.clipChunks = [];
    this.currentClipMeta = { questionId, startedAt: new Date() };

    this.clipRecorder = new MediaRecorder(this.compositeStream, {
      mimeType: 'video/webm;codecs=vp9,opus',
      videoBitsPerSecond: this.opts.videoBitsPerSecond,
    });

    this.clipRecorder.ondataavailable = (e) => {
      if (e.data.size > 0) this.clipChunks.push(e.data);
    };

    this.clipRecorder.start();
  }

  stopQuestionClip(): Promise<RecordingClip> {
    return new Promise((resolve, reject) => {
      if (!this.clipRecorder || !this.currentClipMeta) {
        reject(new Error('Aktif clip kaydı yok'));
        return;
      }

      const meta = this.currentClipMeta;

      this.clipRecorder.onstop = () => {
        const stoppedAt = new Date();
        const duration = Math.round(
          (stoppedAt.getTime() - meta.startedAt.getTime()) / 1000
        );

        // Video blob (ekran + webcam PiP)
        const videoBlob = new Blob(this.clipChunks, { type: 'video/webm' });

        // Ses blob'u: audio track'i ayrı kaydet
        // (Transkripsiyon için ses kanalı gerekli)
        const audioBlob = this.extractAudioBlob(this.clipChunks);

        this.clipChunks = [];
        this.currentClipMeta = null;

        resolve({
          questionId: meta.questionId,
          videoBlob,
          audioBlob,
          startedAt: meta.startedAt,
          stoppedAt,
          duration,
        });
      };

      this.clipRecorder.stop();
    });
  }

  private extractAudioBlob(chunks: Blob[]): Blob {
    // Video chunks'tan ses kanalını ayırmak için
    // WebM container ses track'ini de içerir
    // API tarafında ffmpeg ile ayrıştırılabilir
    // Şimdilik aynı blob'u ses olarak da kullan
    return new Blob(chunks, { type: 'video/webm' });
  }

  // ──────────────────────────────────────────
  // ADIM 5: Mülakatı bitir
  // ──────────────────────────────────────────
  stopAll(): Promise<Blob> {
    return new Promise((resolve) => {
      // Animasyon döngüsünü durdur
      if (this.animationId) {
        cancelAnimationFrame(this.animationId);
        this.animationId = null;
      }

      // Clip recorder'ı durdur (eğer aktifse)
      if (this.clipRecorder?.state === 'recording') {
        this.clipRecorder.stop();
      }

      // Sürekli kaydı durdur
      if (this.continuousRecorder?.state === 'recording') {
        this.continuousRecorder.onstop = () => {
          const fullBlob = new Blob(this.continuousChunks, { type: 'video/webm' });
          this.cleanup();
          resolve(fullBlob);
        };
        this.continuousRecorder.stop();
      } else {
        this.cleanup();
        resolve(new Blob([]));
      }
    });
  }

  private cleanup(): void {
    // Tüm stream track'lerini kapat
    this.screenStream?.getTracks().forEach(t => t.stop());
    this.cameraStream?.getTracks().forEach(t => t.stop());
    this.compositeStream?.getTracks().forEach(t => t.stop());

    // Event listener'ları temizle
    document.removeEventListener('visibilitychange', this.handleVisibilityChange);

    // Referansları sıfırla
    this.screenStream = null;
    this.cameraStream = null;
    this.compositeStream = null;
    this.canvas = null;
    this.ctx = null;
    this.screenVideo = null;
    this.cameraVideo = null;
  }

  // ──────────────────────────────────────────
  // Sekme değişimi yönetimi
  // ──────────────────────────────────────────
  private handleVisibilityChange = (): void => {
    const isHidden = document.hidden;
    this.opts.onTabSwitch(isHidden);
    // NOT: Kayıt devam eder, sadece kullanıcı bilgilendirilir
  };

  private handleScreenShareStopped(): void {
    // Kullanıcı ekran paylaşımını tarayıcıdan durdurursa
    console.warn('Ekran paylaşımı kullanıcı tarafından durduruldu');
    // Bu durumu yukarı ilet (event veya callback ile)
  }

  // Anlık önizleme için canvas element'ini döndür
  getPreviewCanvas(): HTMLCanvasElement | null {
    return this.canvas;
  }

  // Webcam önizlemesi için (mülakat arayüzünde küçük önizleme)
  getCameraStream(): MediaStream | null {
    return this.cameraStream;
  }
}
```

## Upload Stratejisi

```typescript
// src/lib/video/upload.ts

import { DualStreamRecorder, RecordingClip } from './dual-stream-recorder';

export async function uploadQuestionClip(
  clip: RecordingClip,
  interviewId: string
): Promise<{ videoUrl: string; audioUrl: string }> {
  // Presigned URL al
  const { videoUploadUrl, audioUploadUrl, videoKey, audioKey } =
    await fetch(`/api/interview/${interviewId}/upload-urls`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        questionId: clip.questionId,
        videoSize: clip.videoBlob.size,
        audioSize: clip.audioBlob.size,
      }),
    }).then(r => r.json());

  // Paralel upload
  await Promise.all([
    fetch(videoUploadUrl, { method: 'PUT', body: clip.videoBlob }),
    fetch(audioUploadUrl, { method: 'PUT', body: clip.audioBlob }),
  ]);

  return {
    videoUrl: `https://${process.env.NEXT_PUBLIC_S3_BUCKET}.s3.amazonaws.com/${videoKey}`,
    audioUrl: `https://${process.env.NEXT_PUBLIC_S3_BUCKET}.s3.amazonaws.com/${audioKey}`,
  };
}

export async function uploadFullRecording(
  blob: Blob,
  interviewId: string
): Promise<string> {
  const { uploadUrl, key } = await fetch(
    `/api/interview/${interviewId}/upload-full-recording`,
    { method: 'POST', headers: { 'Content-Type': 'application/json' } }
  ).then(r => r.json());

  await fetch(uploadUrl, { method: 'PUT', body: blob });

  return `https://${process.env.NEXT_PUBLIC_S3_BUCKET}.s3.amazonaws.com/${key}`;
}
```

## React Hook

```typescript
// src/hooks/use-dual-stream-recorder.ts

import { useRef, useState, useCallback } from 'react';
import { DualStreamRecorder, RecordingClip } from '@/lib/video/dual-stream-recorder';
import { uploadQuestionClip, uploadFullRecording } from '@/lib/video/upload';
import toast from 'react-hot-toast';

export type RecorderState =
  | 'idle'
  | 'initializing'
  | 'recording'
  | 'stopping';

export function useDualStreamRecorder(interviewId: string) {
  const recorderRef = useRef<DualStreamRecorder | null>(null);
  const [state, setState] = useState<RecorderState>('idle');
  const [tabSwitchCount, setTabSwitchCount] = useState(0);

  const initialize = useCallback(async () => {
    setState('initializing');
    try {
      recorderRef.current = new DualStreamRecorder({
        pipPosition: 'bottom-right',
        pipWidth: 200,
        pipHeight: 150,
        frameRate: 30,
        onTabSwitch: (hidden) => {
          if (hidden) {
            setTabSwitchCount(c => c + 1);
            toast('Başka sekmeye geçtiniz. Kayıt devam ediyor.', {
              icon: '⚠️',
              duration: 3000,
            });
          }
        },
      });

      await recorderRef.current.initialize();
      recorderRef.current.startContinuousRecording();
      setState('recording');
    } catch (err: any) {
      setState('idle');
      if (err.name === 'NotAllowedError') {
        toast.error('Ekran paylaşımı veya kamera izni reddedildi.');
      } else {
        toast.error('Kayıt başlatılamadı: ' + err.message);
      }
      throw err;
    }
  }, []);

  const startClip = useCallback((questionId: string) => {
    recorderRef.current?.startQuestionClip(questionId);
  }, []);

  const stopClipAndSubmit = useCallback(async (questionId: string) => {
    if (!recorderRef.current) return null;

    const clip = await recorderRef.current.stopQuestionClip();

    // Arka planda upload (kullanıcı beklemez)
    uploadQuestionClip(clip, interviewId)
      .then(({ videoUrl, audioUrl }) => {
        // Transkripsiyon başlat
        fetch(`/api/interview/${interviewId}/submit-answer`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            questionId,
            videoUrl,
            audioUrl,
            duration: clip.duration,
            startedAt: clip.startedAt.toISOString(),
            submittedAt: clip.stoppedAt.toISOString(),
          }),
        });
      })
      .catch(err => console.error('Upload hatası:', err));

    return clip;
  }, [interviewId]);

  const stopAll = useCallback(async () => {
    if (!recorderRef.current) return;
    setState('stopping');

    const fullBlob = await recorderRef.current.stopAll();

    // Sürekli kaydı yükle
    if (fullBlob.size > 0) {
      await uploadFullRecording(fullBlob, interviewId);
    }

    setState('idle');
  }, [interviewId]);

  return {
    state,
    tabSwitchCount,
    initialize,
    startClip,
    stopClipAndSubmit,
    stopAll,
    getPreviewCanvas: () => recorderRef.current?.getPreviewCanvas() ?? null,
    getCameraStream: () => recorderRef.current?.getCameraStream() ?? null,
  };
}
```

## Sekme Değişimi Davranışı

Kullanıcı başka sekmeye geçtiğinde:

1. `document.visibilitychange` event'i tetiklenir
2. `onTabSwitch(true)` callback'i çalışır
3. Kullanıcıya toast bildirimi gösterilir: `"Başka sekmeye geçtiniz. Kayıt devam ediyor."`
4. Sekme geçiş sayısı artırılır (`tabSwitchCount`)
5. Kayıt **durmaz** — ekran paylaşımı OS seviyesinde çalışır
6. Raporda `tabSwitchCount` gösterilir (değerlendirmede not olarak)

## Tarayıcı Uyumluluğu

| Tarayıcı | getDisplayMedia | MediaRecorder (WebM) | Durum |
|----------|----------------|----------------------|-------|
| Chrome 90+ | ✓ | ✓ | Tam destek |
| Edge 90+ | ✓ | ✓ | Tam destek |
| Firefox 110+ | ✓ | ✓ | Tam destek |
| Safari 16+ | Kısmi | Kısmi (MP4) | Dikkat: codec sorunu |
| Mobil | ✗ | - | Desteklenmez |

Safari için fallback: `mimeType` olarak `video/mp4` dene, başarısız olursa kullanıcıyı Chrome'a yönlendir.

```typescript
function getSupportedMimeType(): string {
  const types = [
    'video/webm;codecs=vp9,opus',
    'video/webm;codecs=vp8,opus',
    'video/webm',
    'video/mp4',
  ];
  return types.find(t => MediaRecorder.isTypeSupported(t)) ?? 'video/webm';
}
```

## KVKK Uyumu

Video ve ses dosyaları kullanıcıya aittir. `DELETE /api/user/account` endpoint'i çağrıldığında:

1. S3'teki tüm kullanıcı dosyaları silinir (prefix: `users/{userId}/`)
2. Veritabanındaki tüm kayıtlar silinir (cascade)
3. İşlem `AdminLog`'a kaydedilir

Kullanıcı, mülakat raporundan video clip'lerini ayrıca silebilir.
