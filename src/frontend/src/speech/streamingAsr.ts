export type StreamingAsrStatus =
  | 'connecting'
  | 'connected'
  | 'reconnecting'
  | 'error'
  | 'stopped'

export interface AsrFinalSegment {
  start_ms: number
  end_ms: number
  text: string
}

export interface AsrFinalPayload {
  segments: AsrFinalSegment[]
  stats?: {
    wpm?: number
    filler_count?: number
    pause_count?: number
    pause_ms?: number
  }
}

export interface ConnectStreamingAsrOptions {
  url: string
  sessionId: string
  lang: 'tr' | 'en'
  model?: string
  task?: 'transcribe' | 'translate'
  useVad?: boolean
  onPartial: (text: string, tMs?: number) => void
  onFinal: (payload: AsrFinalPayload) => void
  onError: (message: string) => void
  onNotice?: (message: string | null) => void
  onStatus?: (status: StreamingAsrStatus) => void
  mediaStream?: MediaStream
}

export interface StreamingAsrConnection {
  stop(): Promise<void>
}

const TARGET_SAMPLE_RATE = 16000
const CHUNK_MS = 250
const CHUNK_SAMPLES = (TARGET_SAMPLE_RATE * CHUNK_MS) / 1000
const WS_MAX_BUFFERED_AMOUNT = 1_000_000
const WS_MAX_QUEUE = 24
const RECONNECT_DELAYS_MS = [1000, 2000, 4000]

type AudioFrameHandler = (samples: Float32Array) => void

interface AudioPipeline {
  stop(): void
}

function toWsUrl(baseUrl: string, sessionId: string): string {
  const withProto = baseUrl.startsWith('ws://') || baseUrl.startsWith('wss://')
    ? baseUrl
    : baseUrl.replace(/^http:\/\//i, 'ws://').replace(/^https:\/\//i, 'wss://')
  const root = withProto.replace(/\/$/, '')
  return `${root}/ws/transcribe?session_id=${encodeURIComponent(sessionId)}`
}

function floatToInt16Pcm(input: Float32Array): Int16Array {
  const out = new Int16Array(input.length)
  for (let i = 0; i < input.length; i += 1) {
    const clamped = Math.max(-1, Math.min(1, input[i]))
    out[i] = clamped < 0 ? Math.round(clamped * 32768) : Math.round(clamped * 32767)
  }
  return out
}

function resampleLinear(input: Float32Array, inputRate: number, outputRate: number): Float32Array {
  if (inputRate === outputRate) return input
  if (input.length === 0) return new Float32Array(0)

  const outputLength = Math.max(1, Math.floor(input.length * outputRate / inputRate))
  const output = new Float32Array(outputLength)
  const ratio = inputRate / outputRate

  for (let i = 0; i < outputLength; i += 1) {
    const srcPos = i * ratio
    const idx = Math.floor(srcPos)
    const frac = srcPos - idx
    const a = input[idx] ?? input[input.length - 1]
    const b = input[idx + 1] ?? a
    output[i] = a + (b - a) * frac
  }

  return output
}

function int16ToBase64(data: Int16Array): string {
  const bytes = new Uint8Array(data.buffer)
  let binary = ''
  const step = 0x8000
  for (let i = 0; i < bytes.length; i += step) {
    const slice = bytes.subarray(i, i + step)
    binary += String.fromCharCode(...slice)
  }
  return btoa(binary)
}

async function createAudioPipeline(
  stream: MediaStream,
  onFrame: AudioFrameHandler
): Promise<AudioPipeline> {
  const audioContext = new AudioContext()
  const source = audioContext.createMediaStreamSource(stream)
  const sampleRate = audioContext.sampleRate

  const cleanup: Array<() => void> = []

  const buildWorklet = async (): Promise<AudioPipeline> => {
    const processorCode = `
      class PcmCaptureProcessor extends AudioWorkletProcessor {
        process(inputs) {
          const input = inputs[0]
          if (input && input[0]) {
            const channel = input[0]
            this.port.postMessage(channel)
          }
          return true
        }
      }
      registerProcessor('pcm-capture-processor', PcmCaptureProcessor)
    `
    const blob = new Blob([processorCode], { type: 'application/javascript' })
    const moduleUrl = URL.createObjectURL(blob)
    await audioContext.audioWorklet.addModule(moduleUrl)
    URL.revokeObjectURL(moduleUrl)

    const node = new AudioWorkletNode(audioContext, 'pcm-capture-processor')
    node.port.onmessage = (event: MessageEvent<Float32Array>) => {
      const mono = event.data
      if (!mono) return
      const copy = new Float32Array(mono.length)
      copy.set(mono)
      const resampled = resampleLinear(copy, sampleRate, TARGET_SAMPLE_RATE)
      onFrame(resampled)
    }
    const silentGain = audioContext.createGain()
    silentGain.gain.value = 0
    source.connect(node)
    node.connect(silentGain)
    silentGain.connect(audioContext.destination)

    cleanup.push(() => {
      try {
        source.disconnect(node)
      } catch {
        // no-op
      }
      try {
        node.disconnect()
        silentGain.disconnect()
      } catch {
        // no-op
      }
    })

    return {
      stop: () => {
        cleanup.forEach((fn) => fn())
        cleanup.length = 0
        void audioContext.close()
      }
    }
  }

  const buildScriptProcessor = (): AudioPipeline => {
    const processor = audioContext.createScriptProcessor(4096, 1, 1)
    processor.onaudioprocess = (event: AudioProcessingEvent) => {
      const input = event.inputBuffer.getChannelData(0)
      const mono = new Float32Array(input.length)
      mono.set(input)
      const resampled = resampleLinear(mono, sampleRate, TARGET_SAMPLE_RATE)
      onFrame(resampled)
    }

    const silentGain = audioContext.createGain()
    silentGain.gain.value = 0
    source.connect(processor)
    processor.connect(silentGain)
    silentGain.connect(audioContext.destination)

    cleanup.push(() => {
      try {
        source.disconnect(processor)
      } catch {
        // no-op
      }
      try {
        processor.disconnect()
        silentGain.disconnect()
      } catch {
        // no-op
      }
    })

    return {
      stop: () => {
        cleanup.forEach((fn) => fn())
        cleanup.length = 0
        void audioContext.close()
      }
    }
  }

  if (audioContext.audioWorklet && typeof AudioWorkletNode !== 'undefined') {
    try {
      return await buildWorklet()
    } catch {
      return buildScriptProcessor()
    }
  }

  return buildScriptProcessor()
}

export async function connectStreamingAsr(options: ConnectStreamingAsrOptions): Promise<StreamingAsrConnection> {
  const {
    url,
    sessionId,
    lang,
    model,
    task,
    useVad,
    onPartial,
    onFinal,
    onError,
    onNotice,
    onStatus,
    mediaStream
  } = options

  let ws: WebSocket | null = null
  let stopped = false
  let expectedClose = false
  let reconnectAttempt = 0
  let seq = 0

  let acquiredStream: MediaStream | null = null
  let ownsStream = false
  let audioPipeline: AudioPipeline | null = null
  let sampleQueue: number[] = []
  const sendQueue: string[] = []
  let lastQueueWarningAt = 0
  let lastPendingConnectionNoticeAt = 0

  const flushSendQueue = () => {
    if (!ws || ws.readyState !== WebSocket.OPEN) return
    while (sendQueue.length > 0 && ws.bufferedAmount < WS_MAX_BUFFERED_AMOUNT) {
      const message = sendQueue.shift()
      if (!message) break
      ws.send(message)
    }
    if (sendQueue.length === 0) {
      onNotice?.(null)
    }
  }

  const enqueueWsMessage = (message: string, highPriority = false) => {
    if (highPriority) {
      sendQueue.unshift(message)
    } else {
      if (sendQueue.length >= WS_MAX_QUEUE) {
        sendQueue.shift()
        const now = Date.now()
        if (now - lastQueueWarningAt > 5000) {
          lastQueueWarningAt = now
          console.warn('Streaming ASR send queue full; dropping oldest queued audio chunk.')
          onNotice?.('Live transcript is catching up; some queued audio was trimmed on the client.')
        }
      }
      sendQueue.push(message)
    }
    if ((!ws || ws.readyState !== WebSocket.OPEN) && sendQueue.length >= Math.ceil(WS_MAX_QUEUE / 2)) {
      const now = Date.now()
      if (now - lastPendingConnectionNoticeAt > 3000) {
        lastPendingConnectionNoticeAt = now
        onNotice?.('Connecting to live transcript service; audio is buffering locally.')
      }
    }
    flushSendQueue()
  }

  const pushAudio = (samples16k: Float32Array) => {
    if (stopped) return

    for (let i = 0; i < samples16k.length; i += 1) {
      sampleQueue.push(samples16k[i])
    }

    while (sampleQueue.length >= CHUNK_SAMPLES) {
      if (ws && ws.readyState === WebSocket.OPEN && ws.bufferedAmount > WS_MAX_BUFFERED_AMOUNT) {
        sampleQueue.splice(0, CHUNK_SAMPLES)
        continue
      }

      const chunk = sampleQueue.splice(0, CHUNK_SAMPLES)
      const pcm = floatToInt16Pcm(Float32Array.from(chunk))
      const payload = JSON.stringify({
        type: 'audio',
        seq,
        data_b64: int16ToBase64(pcm)
      })
      seq += 1
      enqueueWsMessage(payload, false)
    }
  }

  const connectWs = () => {
    if (stopped) return
    onStatus?.(reconnectAttempt > 0 ? 'reconnecting' : 'connecting')
    const wsUrl = toWsUrl(url, sessionId)
    ws = new WebSocket(wsUrl)

    ws.onopen = () => {
      reconnectAttempt = 0
      onStatus?.('connected')
      onNotice?.(null)
      ws?.send(JSON.stringify({
        type: 'config',
        language: lang,
        ...(model ? { model } : {}),
        task: task || 'transcribe',
        use_vad: useVad ?? true
      }))
      flushSendQueue()
    }

    ws.onmessage = (event: MessageEvent<string>) => {
      let data: any
      try {
        data = JSON.parse(event.data)
      } catch {
        return
      }

      if (data?.type === 'partial') {
        onNotice?.(null)
        onPartial(String(data.text || ''), typeof data.t_ms === 'number' ? data.t_ms : undefined)
      } else if (data?.type === 'partial_status') {
        onNotice?.(String(data.text || 'Processing audio...'))
      } else if (data?.type === 'final') {
        const segments = Array.isArray(data.segments) ? data.segments : []
        onFinal({
          segments: segments.map((segment: any) => ({
            start_ms: Number(segment?.start_ms ?? 0),
            end_ms: Number(segment?.end_ms ?? 0),
            text: String(segment?.text ?? '')
          })),
          stats: data?.stats ?? undefined
        })
      } else if (data?.type === 'error') {
        const detail = String(data?.detail || data?.error || 'Streaming ASR error.')
        if (data?.retryable) {
          onNotice?.(detail)
        } else {
          onError(detail)
        }
      }
    }

    ws.onerror = () => {
      onNotice?.('Streaming ASR connection interrupted. Retrying...')
    }

    ws.onclose = () => {
      if (stopped || expectedClose) {
        onStatus?.('stopped')
        return
      }

      if (reconnectAttempt >= RECONNECT_DELAYS_MS.length) {
        onStatus?.('error')
        onError('Streaming ASR disconnected after retries.')
        return
      }

      const delayMs = RECONNECT_DELAYS_MS[reconnectAttempt]
      reconnectAttempt += 1
      onStatus?.('reconnecting')
      setTimeout(() => {
        connectWs()
      }, delayMs)
    }
  }

  try {
    acquiredStream = mediaStream ?? await navigator.mediaDevices.getUserMedia({ audio: true })
    ownsStream = !mediaStream
  } catch (error) {
    onStatus?.('error')
    onError('Microphone permission denied for live transcript.')
    throw error instanceof Error ? error : new Error('Microphone permission denied')
  }

  audioPipeline = await createAudioPipeline(acquiredStream, pushAudio)
  connectWs()

  return {
    async stop() {
      if (stopped) return
      stopped = true
      expectedClose = true
      sampleQueue = []
      sendQueue.length = 0
      onNotice?.(null)

      if (ws && ws.readyState === WebSocket.OPEN) {
        try {
          ws.send(JSON.stringify({ type: 'end' }))
        } catch {
          // no-op
        }
      }

      if (audioPipeline) {
        audioPipeline.stop()
        audioPipeline = null
      }

      if (ownsStream && acquiredStream) {
        acquiredStream.getTracks().forEach((track) => {
          try {
            track.stop()
          } catch {
            // no-op
          }
        })
      }
      acquiredStream = null

      if (ws) {
        try {
          ws.close()
        } catch {
          // no-op
        }
        ws = null
      }

      onStatus?.('stopped')
    }
  }
}
