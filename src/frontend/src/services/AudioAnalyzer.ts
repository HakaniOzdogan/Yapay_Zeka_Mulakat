/**
 * Real-time audio metrics from media stream
 */

export interface AudioMetrics {
  volumeRms: number; // 0-1
  isSpeaking: boolean;
}

export class AudioAnalyzer {
  private audioContext: AudioContext | null = null;
  private source: MediaStreamAudioSourceNode | null = null;
  private analyser: AnalyserNode | null = null;
  private dataArray: Uint8Array | null = null;

  constructor(stream: MediaStream) {
    this.audioContext = new (window.AudioContext || (window as any).webkitAudioContext)();
    this.source = this.audioContext.createMediaStreamSource(stream);
    this.analyser = this.audioContext.createAnalyser();
    this.analyser.fftSize = 2048;
    this.source.connect(this.analyser);

    this.dataArray = new Uint8Array(this.analyser.frequencyBinCount);
  }

  analyze(): AudioMetrics {
    if (!this.analyser || !this.dataArray) {
      return { volumeRms: 0, isSpeaking: false };
    }

    this.analyser.getByteFrequencyData(this.dataArray as unknown as Uint8Array<ArrayBuffer>);

    // Compute RMS energy
    let sum = 0;
    for (let i = 0; i < this.dataArray.length; i++) {
      const normalized = this.dataArray[i] / 255;
      sum += normalized * normalized;
    }
    const rms = Math.sqrt(sum / this.dataArray.length);

    // Simple speech detection: if RMS > threshold
    const isSpeaking = rms > 0.1;

    return {
      volumeRms: Math.min(1, rms * 2),
      isSpeaking
    };
  }

  dispose() {
    try {
      this.source?.disconnect();
    } catch {
      // no-op
    }
    try {
      this.analyser?.disconnect();
    } catch {
      // no-op
    }
    try {
      this.audioContext?.close();
    } catch {
      // no-op
    }

    this.source = null;
    this.analyser = null;
    this.audioContext = null;
    this.dataArray = null;
  }
}
