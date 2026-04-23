import { useState, useEffect, useRef } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import ApiService, { LiveAnalysisResponse } from '../services/ApiService'
import { initMediaPipe, detectLandmarks, isMediaPipeReady } from '../services/MediaPipeService'
import { MetricsComputer, Metrics, BehaviorStats } from '../services/MetricsComputer'
import { generateCoachingHints, CoachingHint } from '../services/CoachingHints'
import { AudioAnalyzer } from '../services/AudioAnalyzer'
import {
  connectStreamingAsr,
  getSpeechModelLabel,
  getSpeechReadinessMessage,
  getSpeechRetryNotice,
  getStreamingAsrReadiness,
  StreamingAsrConnection,
  StreamingAsrError,
  SpeechReadinessReason,
  StreamingAsrStatus,
  SpeechDiagnostics
} from '../speech/streamingAsr'
import { SessionTransport, SessionTransportStats } from '../services/SessionTransport'
import { FixedRollingBuffer } from '../vision/buffer'
import {
  computeEyeContactScore,
  computeEyeOpenness,
  computeFidgetScore,
  computeHeadJitter,
  computeHeadPose,
  computePostureScore,
  smooth
} from '../vision/features'
import { Calibration, HeadPose, PoseLandmarks, VisionMetrics } from '../vision/types'
import { VideoCanvas } from '../components/VideoCanvas'
import { LiveHints } from '../components/LiveHints'
import { TranscriptModal } from '../components/TranscriptModal'
import '../styles/pages.css'

const VISION_DEBUG_OVERLAY = true
const DEBUG_TRANSPORT = true
const CALIBRATION_DURATION_MS = 5000
const VISION_EVENT_INTERVAL_MS = 500
const ROLLING_SECONDS = 5
const APPROX_FPS = 30
const VISION_BUFFER_SIZE = ROLLING_SECONDS * APPROX_FPS
const METRIC_SMOOTH_ALPHA = 0.2
const ASR_BOOTSTRAP_RETRY_MS = 5000

interface WarningHistoryItem {
  id: string
  type: 'warning' | 'info'
  message: string
  time: string
}

type AudioActivityState =
  | 'idle'
  | 'capturing'
  | 'receiving-partials'
  | 'awaiting-final'
  | 'finalized-recently'
  | 'speech-unavailable'

const ASR_STATUS_LABELS: Record<StreamingAsrStatus, string> = {
  connecting: 'STARTING',
  connected: 'LIVE',
  reconnecting: 'RETRYING',
  error: 'ISSUE',
  stopped: 'STOPPED'
}

const AUDIO_ACTIVITY_LABELS: Record<AudioActivityState, string> = {
  idle: 'Idle',
  capturing: 'Listening for speech',
  'receiving-partials': 'Receiving live partials',
  'awaiting-final': 'Audio received, waiting for a short pause',
  'finalized-recently': 'Recent finalized transcript received',
  'speech-unavailable': 'Speech service unavailable'
}

function formatTranscriptActivity(timestampMs: number | null, now: Date): string {
  if (!timestampMs) return 'not yet'
  const deltaSeconds = Math.max(0, Math.round((now.getTime() - timestampMs) / 1000))
  const clockLabel = new Date(timestampMs).toLocaleTimeString('tr-TR', {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit'
  })
  return deltaSeconds === 0 ? `${clockLabel} (just now)` : `${clockLabel} (${deltaSeconds}s ago)`
}

function deriveAudioActivityState(params: {
  isRecording: boolean
  asrStatus: StreamingAsrStatus
  speechReady: boolean | null
  lastAudioChunkAt: number | null
  lastPartialAt: number | null
  lastFinalAt: number | null
  nowMs: number
}): AudioActivityState {
  const {
    isRecording,
    asrStatus,
    speechReady,
    lastAudioChunkAt,
    lastPartialAt,
    lastFinalAt,
    nowMs
  } = params

  if (!isRecording) return 'idle'
  if (speechReady === false || asrStatus === 'error') return 'speech-unavailable'
  if (lastFinalAt && nowMs - lastFinalAt <= 6000) return 'finalized-recently'
  if (lastPartialAt && nowMs - lastPartialAt <= 5000) return 'receiving-partials'
  if (lastAudioChunkAt && nowMs - lastAudioChunkAt <= 5000) return 'awaiting-final'
  return 'capturing'
}

function normalizeTranscriptText(text: string): string {
  return text.trim().toLocaleLowerCase('tr-TR').replace(/\s+/g, ' ')
}

function appendUniqueTranscriptLines(previous: string[], nextLines: string[]): string[] {
  const merged = [...previous]
  for (const line of nextLines) {
    const trimmed = line.trim()
    if (!trimmed) continue
    const lastLine = merged.length > 0 ? merged[merged.length - 1] : null
    if (lastLine && normalizeTranscriptText(lastLine) === normalizeTranscriptText(trimmed)) {
      continue
    }
    merged.push(trimmed)
  }
  return merged.slice(-5)
}

function hashTranscriptSeed(seed: string, offset: number): string {
  let hash = 0x811c9dc5 ^ offset
  for (let i = 0; i < seed.length; i += 1) {
    hash ^= seed.charCodeAt(i)
    hash = Math.imul(hash, 0x01000193)
  }
  return (hash >>> 0).toString(16).padStart(8, '0')
}

function buildDeterministicTranscriptSegmentId(startMs: number, endMs: number, text: string): string {
  const normalizedText = normalizeTranscriptText(text)
  const seed = `${startMs}:${endMs}:${normalizedText}`
  const part1 = hashTranscriptSeed(seed, 0)
  const part2 = hashTranscriptSeed(seed, 1)
  const part3 = hashTranscriptSeed(seed, 2)
  const part4 = hashTranscriptSeed(seed, 3)
  return [
    part1,
    part2.slice(0, 4),
    `5${part2.slice(1, 4)}`,
    `${((parseInt(part3[0], 16) & 0x3) | 0x8).toString(16)}${part3.slice(1, 4)}`,
    `${part3.slice(4)}${part4}`
  ].join('-')
}

function InterviewSession() {
  const { sessionId } = useParams<{ sessionId: string }>()
  const navigate = useNavigate()
  const [session, setSession] = useState<any>(null)
  const [questions, setQuestions] = useState<any[]>([])
  const [currentQuestionIndex, setCurrentQuestionIndex] = useState(0)
  const [isRecording, setIsRecording] = useState(false)
  const [loading, setLoading] = useState(true)
  const [mediaReady, setMediaReady] = useState(false)
  const [currentMetrics, setCurrentMetrics] = useState<Metrics>({
    eyeContact: 0,
    headStability: 0,
    posture: 0,
    fidget: 0,
    eyeOpenness: 0,
    emotion: 'Neutral',
    timestamp: 0
  })
  const [behaviorStats, setBehaviorStats] = useState<BehaviorStats>({
    dominantEmotion: 'Neutral',
    currentEmotion: 'Neutral',
    emotionPercentages: {
      Neutral: 0,
      Happy: 0,
      Sad: 0,
      Angry: 0,
      Tense: 0,
      Surprised: 0,
      LowEnergy: 0
    },
    eyeOpenPercent: 0,
    eyeClosedPercent: 0,
    blinkCount: 0,
    currentEyesOpen: true
  })
  const [coaching, setCoaching] = useState<CoachingHint[]>([])
  const [showTranscript, setShowTranscript] = useState(false)
  const [transcriptData, setTranscriptData] = useState<any>(null)
  const [uploading, setUploading] = useState(false)
  const [backgroundTranscribes, setBackgroundTranscribes] = useState(0)
  const [videoStream, setVideoStream] = useState<MediaStream | null>(null)
  const [showOverlay, setShowOverlay] = useState(true)
  const [showFaceOverlay, setShowFaceOverlay] = useState(true)
  const [showPoseOverlay, setShowPoseOverlay] = useState(true)
  const [showDiagnosticsOverlay, setShowDiagnosticsOverlay] = useState(true)
  const [faceLandmarks, setFaceLandmarks] = useState<any[] | undefined>(undefined)
  const [poseLandmarks, setPoseLandmarks] = useState<any[] | undefined>(undefined)
  const [llmInsight, setLlmInsight] = useState<LiveAnalysisResponse | null>(null)
  const [liveTranscriptLines, setLiveTranscriptLines] = useState<string[]>([])
  const [liveTranscriptInterim, setLiveTranscriptInterim] = useState('')
  const [asrStatus, setAsrStatus] = useState<StreamingAsrStatus>('stopped')
  const [asrError, setAsrError] = useState<string | null>(null)
  const [asrNotice, setAsrNotice] = useState<string | null>(null)
  const [speechReady, setSpeechReady] = useState<boolean | null>(null)
  const [speechReadinessReason, setSpeechReadinessReason] = useState<SpeechReadinessReason | null>(null)
  const [speechReadyMessage, setSpeechReadyMessage] = useState<string | null>(null)
  const [lastPartialAt, setLastPartialAt] = useState<number | null>(null)
  const [lastFinalAt, setLastFinalAt] = useState<number | null>(null)
  const [lastAudioChunkAt, setLastAudioChunkAt] = useState<number | null>(null)
  const [audioActivityState, setAudioActivityState] = useState<AudioActivityState>('idle')
  const [clockNow, setClockNow] = useState(new Date())
  const [warningHistory, setWarningHistory] = useState<WarningHistoryItem[]>([])
  const [speechDiagnostics, setSpeechDiagnostics] = useState<SpeechDiagnostics | null>(null)
  const [visionMetrics, setVisionMetrics] = useState<VisionMetrics>({
    eyeContact: 0,
    posture: 0,
    fidget: 0,
    headJitter: 0,
    eyeOpenness: 0,
    calibrated: false
  })
  const [visionCalibration, setVisionCalibration] = useState<Calibration | null>(null)
  const [transportStats, setTransportStats] = useState<SessionTransportStats>({
    queued: 0,
    sent: 0,
    dropped: 0,
    failedBatches: 0
  })

  const metricsComputerRef = useRef<MetricsComputer | null>(null)
  const audioAnalyzerRef = useRef<AudioAnalyzer | null>(null)
  const mediaRecorderRef = useRef<MediaRecorder | null>(null)
  const audioChunksRef = useRef<Blob[]>([])
  const recordingMimeTypeRef = useRef('audio/webm')
  const frameCountRef = useRef(0)
  const mediaStreamRef = useRef<MediaStream | null>(null)
  const lastPoseLandmarksRef = useRef<any[] | null>(null)
  const poseMissFramesRef = useRef(0)
  const liveAnalysisIntervalRef = useRef<ReturnType<typeof setInterval> | null>(null)
  const metricsWindowRef = useRef<Metrics[]>([])
  const behaviorStatsRef = useRef<BehaviorStats>(behaviorStats)
  const isRecordingRef = useRef(false)
  const asrConnectionRef = useRef<StreamingAsrConnection | null>(null)
  const asrBootstrapTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const asrBootstrapInFlightRef = useRef(false)
  const sessionTransportRef = useRef<SessionTransport | null>(null)
  const transportStatsIntervalRef = useRef<ReturnType<typeof setInterval> | null>(null)
  const warningSeenAtRef = useRef<Record<string, number>>({})
  const sessionStartAtMsRef = useRef<number | null>(null)
  const lastVisionEventSentAtRef = useRef<number>(0)
  const headPoseBufferRef = useRef(new FixedRollingBuffer<HeadPose>(VISION_BUFFER_SIZE))
  const poseBufferRef = useRef(new FixedRollingBuffer<PoseLandmarks>(VISION_BUFFER_SIZE))
  const smoothedVisionMetricsRef = useRef<Omit<VisionMetrics, 'calibrated'>>({
    eyeContact: 0,
    posture: 0,
    fidget: 0,
    headJitter: 0,
    eyeOpenness: 0
  })
  const calibrationAccumulatorRef = useRef({
    yawSum: 0,
    pitchSum: 0,
    eyeOpennessSum: 0,
    sampleCount: 0
  })
  useEffect(() => {
    loadSession()
    initializeMediaPipe()
  }, [sessionId])

  useEffect(() => {
    behaviorStatsRef.current = behaviorStats
  }, [behaviorStats])

  useEffect(() => {
    isRecordingRef.current = isRecording
  }, [isRecording])

  useEffect(() => {
    const timer = setInterval(() => setClockNow(new Date()), 1000)
    return () => clearInterval(timer)
  }, [])

  useEffect(() => {
    const nextState = deriveAudioActivityState({
      isRecording,
      asrStatus,
      speechReady,
      lastAudioChunkAt,
      lastPartialAt,
      lastFinalAt,
      nowMs: clockNow.getTime()
    })

    setAudioActivityState(prev => prev === nextState ? prev : nextState)
  }, [asrStatus, clockNow, isRecording, lastAudioChunkAt, lastFinalAt, lastPartialAt, speechReady])

  useEffect(() => {
    return () => {
      if (asrBootstrapTimerRef.current) {
        clearTimeout(asrBootstrapTimerRef.current)
        asrBootstrapTimerRef.current = null
      }
      closeMediaCapture()
      void asrConnectionRef.current?.stop()
      asrConnectionRef.current = null
      void sessionTransportRef.current?.stop({ flush: true })
      sessionTransportRef.current = null
      if (transportStatsIntervalRef.current) {
        clearInterval(transportStatsIntervalRef.current)
        transportStatsIntervalRef.current = null
      }
      if (liveAnalysisIntervalRef.current) {
        clearInterval(liveAnalysisIntervalRef.current)
        liveAnalysisIntervalRef.current = null
      }
    }
  }, [])

  const loadSession = async () => {
    if (!sessionId) return
    try {
      const sess = await ApiService.getSession(sessionId)
      setSession(sess)

      let qs = await ApiService.getQuestions(sessionId)
      if (!qs || qs.length === 0) {
        qs = await ApiService.seedQuestions(sessionId)
      }
      setQuestions(qs)
    } catch (error) {
      console.error('Failed to load session:', error)
    } finally {
      setLoading(false)
    }
  }

  const initializeMediaPipe = async () => {
    try {
      await initMediaPipe()
      setMediaReady(true)
      metricsComputerRef.current = new MetricsComputer()
    } catch (error) {
      console.error('Failed to initialize MediaPipe:', error)
      alert('Failed to load vision models. Continuing without real-time metrics.')
    }
  }

  const resetVisionPipeline = () => {
    sessionStartAtMsRef.current = null
    lastVisionEventSentAtRef.current = 0
    headPoseBufferRef.current.clear()
    poseBufferRef.current.clear()
    calibrationAccumulatorRef.current = {
      yawSum: 0,
      pitchSum: 0,
      eyeOpennessSum: 0,
      sampleCount: 0
    }
    smoothedVisionMetricsRef.current = {
      eyeContact: 0,
      posture: 0,
      fidget: 0,
      headJitter: 0,
      eyeOpenness: 0
    }
    setVisionCalibration(null)
    setVisionMetrics({
      eyeContact: 0,
      posture: 0,
      fidget: 0,
      headJitter: 0,
      eyeOpenness: 0,
      calibrated: false
    })
  }

  const resetTranscriptDiagnostics = () => {
    setSpeechReady(null)
    setSpeechReadinessReason(null)
    setSpeechReadyMessage(null)
    setSpeechDiagnostics(null)
    setLastPartialAt(null)
    setLastFinalAt(null)
    setLastAudioChunkAt(null)
    setAudioActivityState('idle')
  }

  const clearAsrBootstrapRetry = () => {
    if (asrBootstrapTimerRef.current) {
      clearTimeout(asrBootstrapTimerRef.current)
      asrBootstrapTimerRef.current = null
    }
  }

  const applySpeechReadiness = (ready: boolean, reason: SpeechReadinessReason, detail?: string | null) => {
    setSpeechReady(ready)
    setSpeechReadinessReason(reason)
    setSpeechReadyMessage(getSpeechReadinessMessage(reason, detail))
  }

  const scheduleAsrBootstrapRetry = (stream: MediaStream, notice: string | null) => {
    if (!isRecordingRef.current || asrBootstrapTimerRef.current) return
    setAsrStatus('reconnecting')
    setAsrNotice(notice)
    asrBootstrapTimerRef.current = setTimeout(() => {
      asrBootstrapTimerRef.current = null
      void startStreamingAsr(stream)
    }, ASR_BOOTSTRAP_RETRY_MS)
  }

  const startRecording = async () => {
    try {
      resetVisionPipeline()
      resetTranscriptDiagnostics()
      sessionStartAtMsRef.current = performance.now()

      if (sessionId) {
        if (sessionTransportRef.current) {
          await sessionTransportRef.current.stop({ flush: false })
        }
        const baseUrl = (import.meta.env.VITE_API_URL || 'http://localhost:8080/api').replace(/\/$/, '')
        const transport = new SessionTransport({
          apiBaseUrl: baseUrl,
          sessionId,
          flushIntervalMs: 500,
          maxBatchSize: 50,
          maxQueue: 500,
          maxRetries: 3
        })
        transport.start()
        sessionTransportRef.current = transport

        if (transportStatsIntervalRef.current) {
          clearInterval(transportStatsIntervalRef.current)
        }
        transportStatsIntervalRef.current = setInterval(() => {
          setTransportStats(transport.getStats())
        }, 500)
      }

      const stream = await navigator.mediaDevices.getUserMedia({
        audio: true,
        video: { width: { ideal: 1280 }, height: { ideal: 720 } }
      })

      mediaStreamRef.current = stream
      setVideoStream(stream)

      // Setup audio recording (best-effort; don't block live video if recorder fails)
      audioChunksRef.current = []
      mediaRecorderRef.current = null
      recordingMimeTypeRef.current = 'audio/webm'
      try {
        const audioTracks = stream.getAudioTracks()
        if (audioTracks.length > 0 && typeof MediaRecorder !== 'undefined') {
          const audioOnlyStream = new MediaStream(audioTracks)
          const mimeCandidates = [
            'audio/webm;codecs=opus',
            'audio/webm',
            'audio/ogg;codecs=opus'
          ]
          const supportedMimeType = mimeCandidates.find(type => MediaRecorder.isTypeSupported(type))
          mediaRecorderRef.current = supportedMimeType
            ? new MediaRecorder(audioOnlyStream, { mimeType: supportedMimeType })
            : new MediaRecorder(audioOnlyStream)
          recordingMimeTypeRef.current = mediaRecorderRef.current.mimeType || supportedMimeType || 'audio/webm'

          mediaRecorderRef.current.ondataavailable = (e) => {
            if (e.data.size > 0) {
              audioChunksRef.current.push(e.data)
            }
          }
          mediaRecorderRef.current.start(1000)
        } else {
          console.warn('Audio track or MediaRecorder unavailable; continuing with live metrics only.')
        }
      } catch (recorderError) {
        console.warn('MediaRecorder init failed; continuing with live metrics only:', recorderError)
      }

      // Initialize audio analyzer
      try {
        audioAnalyzerRef.current = new AudioAnalyzer(stream)
      } catch (audioError) {
        console.warn('Audio analyzer init failed:', audioError)
      }

      setLiveTranscriptLines([])
      setLiveTranscriptInterim('')
      warningSeenAtRef.current = {}
      setWarningHistory([])
      setAsrError(null)
      setAsrNotice(null)
      if (!isMediaPipeReady()) {
        setAsrNotice('Vision modeli hazirlaniyor. Bu sirada audio transcript baglantisi devam edebilir.')
      }
      setIsRecording(true)
      isRecordingRef.current = true
      await startStreamingAsr(stream)
      if (liveAnalysisIntervalRef.current) {
        clearInterval(liveAnalysisIntervalRef.current)
      }
      liveAnalysisIntervalRef.current = setInterval(() => {
        void sendLiveWindowAnalysis()
      }, 15000)
    } catch (error) {
      console.error('Failed to start recording:', error)
      const mediaError = error as DOMException
      if (mediaError?.name === 'NotAllowedError') {
        alert('Microphone/camera permission denied. Please allow access in browser site settings.')
      } else if (mediaError?.name === 'NotFoundError') {
        alert('No microphone/camera device found. Please connect a device and try again.')
      } else if (mediaError?.name === 'NotReadableError') {
        alert('Microphone/camera is busy in another app. Close other apps and try again.')
      } else if (mediaError?.name === 'OverconstrainedError') {
        alert('Requested camera settings are not supported on this device.')
      } else if (mediaError?.name === 'NotSupportedError') {
        alert('Audio recording format is not supported by this browser.')
      } else {
        alert(`Failed to access microphone/camera (${mediaError?.name || 'UnknownError'})`)
      }

      closeMediaCapture()
      setIsRecording(false)
    }
  }

  const stopRecording = async () => {
    clearAsrBootstrapRetry()
    asrBootstrapInFlightRef.current = false
    const transportStopPromise = sessionTransportRef.current?.stop({ flush: true }) ?? Promise.resolve()
    sessionTransportRef.current = null
    if (transportStatsIntervalRef.current) {
      clearInterval(transportStatsIntervalRef.current)
      transportStatsIntervalRef.current = null
    }

    const asrStopPromise = asrConnectionRef.current?.stop() ?? Promise.resolve()
    asrConnectionRef.current = null
    setAsrStatus('stopped')
    setAsrNotice(null)
    resetTranscriptDiagnostics()

    let recorderStopPromise: Promise<unknown> = Promise.resolve()
    if (mediaRecorderRef.current && mediaRecorderRef.current.state !== 'inactive') {
      recorderStopPromise = new Promise(resolve => {
        if (!mediaRecorderRef.current) {
          resolve(null)
          return
        }
        mediaRecorderRef.current.onstop = resolve
      })
      try {
        mediaRecorderRef.current.requestData()
      } catch {
        // Some browsers may throw if requestData is not available at this moment.
      }
      mediaRecorderRef.current.stop()
    }

    closeMediaCapture()
    setFaceLandmarks(undefined)
    setPoseLandmarks(undefined)
    lastPoseLandmarksRef.current = null
    poseMissFramesRef.current = 0

    if (liveAnalysisIntervalRef.current) {
      clearInterval(liveAnalysisIntervalRef.current)
      liveAnalysisIntervalRef.current = null
    }

    setIsRecording(false)
    isRecordingRef.current = false
    await transportStopPromise
    await asrStopPromise
    await recorderStopPromise
  }

  const recoverStreamingAsr = async (stream: MediaStream, error: StreamingAsrError) => {
    if (error.code === 'microphone_permission_denied') {
      setAsrStatus('error')
      setAsrError(error.message)
      applySpeechReadiness(false, 'startup_failed', error.message)
      return
    }

    const previousConnection = asrConnectionRef.current
    asrConnectionRef.current = null

    if (previousConnection) {
      try {
        await previousConnection.stop()
      } catch (stopError) {
        console.warn('Failed to stop previous ASR connection cleanly:', stopError)
      }
    }

    if (!isRecordingRef.current) {
      return
    }

    const speechUrl = import.meta.env.VITE_SPEECH_URL || 'http://localhost:8000'
    const readiness = await getStreamingAsrReadiness(speechUrl)
    setAsrError(null)

    if (readiness.ready) {
      applySpeechReadiness(true, 'ready')
      scheduleAsrBootstrapRetry(stream, 'Canli transcript baglantisi koptu. Yeniden baglaniliyor.')
      return
    }

    applySpeechReadiness(false, readiness.reason, readiness.details?.failureDetail || error.message)
    scheduleAsrBootstrapRetry(stream, getSpeechRetryNotice(readiness.reason))
  }

  const startStreamingAsr = async (stream: MediaStream) => {
    if (!sessionId || !isRecordingRef.current || asrBootstrapInFlightRef.current || asrConnectionRef.current) return

    const speechUrl = import.meta.env.VITE_SPEECH_URL || 'http://localhost:8000'
    clearAsrBootstrapRetry()
    asrBootstrapInFlightRef.current = true
    setAsrError(null)
    setAsrNotice(null)
    setAsrStatus((prev) => prev === 'reconnecting' ? 'reconnecting' : 'connecting')

    try {
      const readiness = await getStreamingAsrReadiness(speechUrl)

      if (!isRecordingRef.current) {
        return
      }

      if (!readiness.ready) {
        applySpeechReadiness(false, readiness.reason, readiness.details?.failureDetail || readiness.message)
        scheduleAsrBootstrapRetry(stream, getSpeechRetryNotice(readiness.reason))
        return
      }

      applySpeechReadiness(true, 'ready')

      const connection = await connectStreamingAsr({
        url: speechUrl,
        sessionId,
        lang: session?.language === 'en' ? 'en' : 'tr',
        task: 'transcribe',
        useVad: true,
        mediaStream: stream,
        onStatus: (status) => {
          setAsrStatus(status)
          if (status === 'connected') {
            applySpeechReadiness(true, 'ready')
            setAsrError(null)
            setAsrNotice(null)
          }
        },
        onAudioChunk: () => {
          setLastAudioChunkAt(Date.now())
        },
        onError: (error) => {
          void recoverStreamingAsr(stream, error)
        },
        onNotice: (message) => {
          setAsrNotice(message)
        },
        onDiagnostics: (diag) => {
          setSpeechDiagnostics(diag)
        },
        onPartial: (text) => {
          const trimmed = text.trim()
          setLiveTranscriptInterim(trimmed)
          if (trimmed.length > 0) {
            setLastPartialAt(Date.now())
          }
        },
        onFinal: (payload) => {
          applySpeechReadiness(true, 'ready')
          setAsrNotice(null)
          setLiveTranscriptInterim('')
          setLastFinalAt(Date.now())
          const finalizedLines = payload.segments
            .map((segment) => segment.text.trim())
            .filter((line) => line.length > 0)

          if (finalizedLines.length > 0) {
            setLiveTranscriptLines((prev) => appendUniqueTranscriptLines(prev, finalizedLines))
          }

          const ingestSegments = payload.segments
            .filter((segment) => segment.text && segment.text.trim().length > 0)
            .map((segment) => ({
              clientSegmentId: buildDeterministicTranscriptSegmentId(
                Math.max(0, Math.round(segment.start_ms)),
                Math.max(0, Math.round(segment.end_ms)),
                segment.text.trim()
              ),
              startMs: Math.max(0, Math.round(segment.start_ms)),
              endMs: Math.max(0, Math.round(segment.end_ms)),
              text: segment.text.trim()
            }))

          if (ingestSegments.length === 0) return

          void ApiService.postTranscriptBatch(sessionId, ingestSegments).catch((error) => {
            console.warn('Failed to push transcript batch:', error)
          })
        }
      })

      if (!isRecordingRef.current) {
        await connection.stop()
        return
      }

      asrConnectionRef.current = connection
    } catch (error) {
      if (!isRecordingRef.current) {
        return
      }

      const message = error instanceof Error && error.message
        ? error.message
        : 'Live transcript could not start. Vision can continue.'
      applySpeechReadiness(false, 'startup_failed', message)
      scheduleAsrBootstrapRetry(stream, getSpeechRetryNotice('startup_failed'))
      console.warn('Streaming ASR startup failed:', error)
    } finally {
      asrBootstrapInFlightRef.current = false
    }
  }

  const handleFrame = async (video: HTMLVideoElement, _canvas: HTMLCanvasElement) => {
    if (!isRecording || !metricsComputerRef.current || !isMediaPipeReady()) {
      return
    }

    frameCountRef.current++

    try {
      const landmarks = await detectLandmarks(video, Date.now())
      const faceLm = landmarks.face?.faceLandmarks?.[0] || []
      const poseRaw = landmarks.pose?.landmarks
      const poseLm = Array.isArray(poseRaw?.[0]) ? poseRaw[0] : Array.isArray(poseRaw) ? poseRaw : []

      let stablePoseLm = poseLm
      if (poseLm.length > 0) {
        lastPoseLandmarksRef.current = poseLm
        poseMissFramesRef.current = 0
      } else if (lastPoseLandmarksRef.current && poseMissFramesRef.current < 20) {
        poseMissFramesRef.current += 1
        stablePoseLm = lastPoseLandmarksRef.current
      } else {
        poseMissFramesRef.current += 1
        stablePoseLm = []
      }

      setFaceLandmarks(faceLm.length > 0 ? faceLm : undefined)
      setPoseLandmarks(stablePoseLm.length > 0 ? stablePoseLm : undefined)

      const nowMs = performance.now()
      const startedAtMs = sessionStartAtMsRef.current ?? nowMs
      const elapsedMs = Math.max(0, Math.round(nowMs - startedAtMs))
      const currentHeadPose = faceLm.length > 0 ? computeHeadPose(faceLm) : { yaw: 0, pitch: 0 }

      if (faceLm.length > 0) {
        headPoseBufferRef.current.push(currentHeadPose)
      }
      if (stablePoseLm.length > 0) {
        poseBufferRef.current.push(stablePoseLm)
      }

      const eyeOpennessRaw = faceLm.length > 0
        ? computeEyeOpenness(faceLm)
        : smoothedVisionMetricsRef.current.eyeOpenness
      const postureRaw = stablePoseLm.length > 0
        ? computePostureScore(stablePoseLm)
        : smoothedVisionMetricsRef.current.posture
      const fidgetRaw = computeFidgetScore(poseBufferRef.current.values())
      const headJitterRaw = computeHeadJitter(headPoseBufferRef.current.values())

      let calibration = visionCalibration
      if (!calibration && elapsedMs <= CALIBRATION_DURATION_MS && faceLm.length > 0) {
        calibrationAccumulatorRef.current.yawSum += currentHeadPose.yaw
        calibrationAccumulatorRef.current.pitchSum += currentHeadPose.pitch
        calibrationAccumulatorRef.current.eyeOpennessSum += eyeOpennessRaw
        calibrationAccumulatorRef.current.sampleCount += 1
      }

      if (!calibration && elapsedMs >= CALIBRATION_DURATION_MS) {
        const sampleCount = calibrationAccumulatorRef.current.sampleCount
        if (sampleCount > 0) {
          calibration = {
            baselineYaw: calibrationAccumulatorRef.current.yawSum / sampleCount,
            baselinePitch: calibrationAccumulatorRef.current.pitchSum / sampleCount,
            baselineEyeOpenness: calibrationAccumulatorRef.current.eyeOpennessSum / sampleCount,
            startedAtMs: startedAtMs,
            completedAtMs: startedAtMs + CALIBRATION_DURATION_MS,
            sampleCount
          }
          setVisionCalibration(calibration)
        }
      }

      const calibrated = !!calibration
      const eyeContactRaw = computeEyeContactScore(currentHeadPose, calibration)
      const prev = smoothedVisionMetricsRef.current
      const smoothed = {
        eyeContact: smooth(prev.eyeContact, eyeContactRaw, METRIC_SMOOTH_ALPHA),
        posture: smooth(prev.posture, postureRaw, METRIC_SMOOTH_ALPHA),
        fidget: smooth(prev.fidget, fidgetRaw, METRIC_SMOOTH_ALPHA),
        headJitter: smooth(prev.headJitter, headJitterRaw, METRIC_SMOOTH_ALPHA),
        eyeOpenness: smooth(prev.eyeOpenness, eyeOpennessRaw, METRIC_SMOOTH_ALPHA)
      }
      smoothedVisionMetricsRef.current = smoothed
      setVisionMetrics({
        ...smoothed,
        calibrated
      })

      if (calibrated && elapsedMs - lastVisionEventSentAtRef.current >= VISION_EVENT_INTERVAL_MS) {
        lastVisionEventSentAtRef.current = elapsedMs
        sessionTransportRef.current?.enqueueEvent({
          clientEventId: crypto.randomUUID(),
          tsMs: elapsedMs,
          source: 'Vision',
          type: 'vision_metrics_v1',
          payload: {
            eyeContact: smoothed.eyeContact,
            posture: smoothed.posture,
            fidget: smoothed.fidget,
            headJitter: smoothed.headJitter,
            eyeOpenness: smoothed.eyeOpenness,
            calibrated: true
          }
        })
      }

      if (faceLm.length > 0) {
        const metrics = metricsComputerRef.current.computeFrame(
          faceLm,
          stablePoseLm
        )

        setCurrentMetrics(metrics)
        setBehaviorStats(metricsComputerRef.current.getBehaviorStats())
        metricsWindowRef.current.push(metrics)
        if (metricsWindowRef.current.length > 600) {
          metricsWindowRef.current.splice(0, metricsWindowRef.current.length - 600)
        }

        // Update coaching hints
        const hints = generateCoachingHints(metrics)
        setCoaching(hints)
        pushWarningHistory(hints)
      }
    } catch (error) {
      console.error('Inference error:', error)
    }
  }

  const closeMediaCapture = () => {
    const stopTracks = (stream: MediaStream | null) => {
      if (!stream) return
      stream.getTracks().forEach(track => {
        try {
          if (track.readyState !== 'ended') track.stop()
        } catch {
          // no-op
        }
      })
    }

    stopTracks(mediaStreamRef.current)
    stopTracks(videoStream)

    mediaStreamRef.current = null
    setVideoStream(null)

    if (audioAnalyzerRef.current) {
      try {
        audioAnalyzerRef.current.dispose?.()
      } catch {
        // no-op
      }
      audioAnalyzerRef.current = null
    }
  }

  const pushWarningHistory = (hints: CoachingHint[]) => {
    const now = Date.now()
    const warningHints = hints.filter(h => h.type === 'warning' || h.type === 'info')
    if (warningHints.length === 0) return

    const additions: WarningHistoryItem[] = []
    for (const hint of warningHints) {
      const key = `${hint.type}:${hint.message}`
      const lastSeenAt = warningSeenAtRef.current[key] || 0
      if (now - lastSeenAt < 4000) continue
      warningSeenAtRef.current[key] = now
      additions.push({
        id: `${key}:${now}`,
        type: hint.type === 'warning' ? 'warning' : 'info',
        message: hint.message,
        time: new Date(now).toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit', second: '2-digit' })
      })
    }

    if (additions.length > 0) {
      setWarningHistory(prev => [...additions, ...prev].slice(0, 20))
    }
  }

  const uploadAndTranscribe = async (chunks: Blob[], showModal: boolean): Promise<boolean> => {
    if (!sessionId || chunks.length === 0) {
      return false
    }

    setUploading(true)
    try {
      // Create FormData with audio blob
      const recordingMimeType = recordingMimeTypeRef.current || 'audio/webm'
      const audioBlob = new Blob(chunks, { type: recordingMimeType })
      const fileExtension = recordingMimeType.includes('ogg')
        ? 'ogg'
        : recordingMimeType.includes('mpeg')
          ? 'mp3'
          : recordingMimeType.includes('mp4')
            ? 'm4a'
            : 'webm'
      const formData = new FormData()
      formData.append('file', audioBlob, `answer.${fileExtension}`)

      // Transcribe using speech-service
      const transcriptResult = await ApiService.transcribeAudio(formData, session?.language || 'tr')

      // Store in state for display
      setTranscriptData(transcriptResult)

      // Store in backend
      if (transcriptResult.segments && sessionId) {
        await ApiService.storeTranscript(sessionId, {
          segments: transcriptResult.segments,
          full_text: transcriptResult.full_text,
          stats: {
            duration_ms: transcriptResult.duration_ms,
            word_count: transcriptResult.word_count,
            wpm: transcriptResult.wpm,
            filler_count: transcriptResult.filler_count,
            pause_count: transcriptResult.pause_count
          }
        })
      }

      if (showModal) {
        setShowTranscript(true)
      }
      return true
    } catch (error) {
      console.error('Transcription failed:', error)
      return false
    } finally {
      setUploading(false)
    }
  }

  const sendLiveWindowAnalysis = async () => {
    if (!sessionId || !session) return
    const windowMetrics = metricsWindowRef.current
    if (windowMetrics.length < 10) return

    const avg = (values: number[]) =>
      values.length === 0 ? 0 : values.reduce((a, b) => a + b, 0) / values.length

    const nowBlinkCount = behaviorStatsRef.current.blinkCount
    const lastWindow = windowMetrics.slice(-120)
    const emotionDistribution: Record<string, number> = {}
    for (const [emotion, value] of Object.entries(behaviorStatsRef.current.emotionPercentages)) {
      emotionDistribution[emotion] = Number(value.toFixed(2))
    }

    try {
      const result = await ApiService.analyzeLiveWindow({
        sessionId,
        windowSec: 15,
        role: session.selectedRole || 'Software Engineer',
        questionPrompt: questions[currentQuestionIndex]?.prompt || '',
        videoMetrics: {
          eyeContactAvg: Number(avg(lastWindow.map(m => m.eyeContact)).toFixed(2)),
          headStabilityAvg: Number(avg(lastWindow.map(m => m.headStability)).toFixed(2)),
          postureAvg: Number(avg(lastWindow.map(m => m.posture)).toFixed(2)),
          fidgetAvg: Number(avg(lastWindow.map(m => m.fidget)).toFixed(2)),
          eyeOpennessAvg: Number(avg(lastWindow.map(m => m.eyeOpenness)).toFixed(2)),
          blinkCountWindow: nowBlinkCount,
          emotionDistribution
        }
      })
      setLlmInsight(result)
      metricsWindowRef.current = []
    } catch (error) {
      console.warn('Live LLM analysis skipped:', error)
    }
  }

  const handleNext = async () => {
    // Stop recording and transcribe before moving to next question
    if (isRecording) {
      await stopRecording()
    }

    const recordedChunks = [...audioChunksRef.current]
    audioChunksRef.current = []
    const isLastQuestion = currentQuestionIndex >= questions.length - 1

    if (recordedChunks.length > 0) {
      if (isLastQuestion) {
        // Final question: wait once so report can include the latest transcript.
        try {
          await uploadAndTranscribe(recordedChunks, false)
        } catch {
          // continue to finalize
        }
      } else {
        // Intermediate questions: do not block UX, transcribe in background.
        setBackgroundTranscribes(prev => prev + 1)
        void uploadAndTranscribe(recordedChunks, false).finally(() => {
          setBackgroundTranscribes(prev => Math.max(0, prev - 1))
        })
      }
    }

    proceedToNext()
  }

  const proceedToNext = () => {
    if (currentQuestionIndex < questions.length - 1) {
      setCurrentQuestionIndex(currentQuestionIndex + 1)
      setShowTranscript(false)
      setTranscriptData(null)
      audioChunksRef.current = []
      setLiveTranscriptLines([])
      setLiveTranscriptInterim('')
      setAsrNotice(null)
      setAsrError(null)
      resetTranscriptDiagnostics()
      warningSeenAtRef.current = {}
      setWarningHistory([])
    } else {
      finalize()
    }
  }

  const finalize = async () => {
    if (!sessionId) return
    try {
      await ApiService.finalizeSession(sessionId)
      navigate(`/report/${sessionId}`)
    } catch (error) {
      console.error('Failed to finalize:', error)
    }
  }

  if (loading) return <div className="page"><p>Loading...</p></div>
  if (!session) return <div className="page"><p>Session not found</p></div>

  const currentQuestion = questions[currentQuestionIndex]
  const minute = clockNow.getMinutes() + clockNow.getSeconds() / 60
  const hour = (clockNow.getHours() % 12) + minute / 60
  const mechanicalClock = {
    hrDeg: hour * 30,
    minDeg: minute * 6
  }
  const calibrationRemainingMs = (() => {
    if (visionCalibration || !sessionStartAtMsRef.current || !isRecording) return 0
    const elapsed = performance.now() - sessionStartAtMsRef.current
    return Math.max(0, Math.round(CALIBRATION_DURATION_MS - elapsed))
  })()
  const sessionElapsedSeconds = sessionStartAtMsRef.current && isRecording
    ? Math.max(0, Math.floor((performance.now() - sessionStartAtMsRef.current) / 1000))
    : 0
  const formattedElapsed = `${Math.floor(sessionElapsedSeconds / 60).toString().padStart(2, '0')}:${(sessionElapsedSeconds % 60).toString().padStart(2, '0')}`
  const eyeContactPercent = Math.round(currentMetrics.eyeContact)
  const pacePercent = Math.round(currentMetrics.headStability)
  const sentimentLabel = llmInsight?.summary || `${behaviorStats.currentEmotion} anlatim`
  const asrStatusLabel = ASR_STATUS_LABELS[asrStatus]
  const speechModelLabel = getSpeechModelLabel(speechReady, speechReadinessReason)
  const audioActivityLabel = AUDIO_ACTIVITY_LABELS[audioActivityState]
  const lastPartialLabel = formatTranscriptActivity(lastPartialAt, clockNow)
  const lastFinalLabel = formatTranscriptActivity(lastFinalAt, clockNow)
  const vadChunkTotal = (speechDiagnostics?.vad_voiced_chunks || 0) + (speechDiagnostics?.vad_rejected_chunks || 0)
  const vadVoicedRatio = vadChunkTotal > 0
    ? Math.round(((speechDiagnostics?.vad_voiced_chunks || 0) / vadChunkTotal) * 100)
    : null
  const vadRejectedRatio = vadChunkTotal > 0
    ? Math.round(((speechDiagnostics?.vad_rejected_chunks || 0) / vadChunkTotal) * 100)
    : null
  const sileroFallbackWarning = speechDiagnostics && !speechDiagnostics.silero_available
    ? 'Silero aktif degil. Transcript kalite modu su anda energy fallback ile calisiyor.'
    : null
  const uploadFormatLabel = speechDiagnostics?.last_upload_container
    ? `${speechDiagnostics.last_upload_container}${speechDiagnostics.last_upload_codec ? ` / ${speechDiagnostics.last_upload_codec}` : ''}`
    : '—'
  const uploadSampleLabel = speechDiagnostics?.last_upload_sample_rate
    ? `${speechDiagnostics.last_upload_sample_rate} Hz / ${speechDiagnostics.last_upload_channels || '?'} ch`
    : '—'
  const speechIssueDetail = (() => {
    if (!isRecording) return null
    switch (speechReadinessReason) {
      case 'unreachable':
        return 'Speech servisine ulasilamiyor. Docker servisleri ve ag erisimi kontrol edilmeli.'
      case 'model_loading':
        return 'Transcript modeli yukleniyor. Hazir oldugunda sayfa yenilemeden otomatik baglanacak.'
      case 'at_capacity':
        return 'Tum transcript oturum slotlari dolu. Kisa bir sure sonra yeniden baglanilacak.'
      case 'startup_failed':
        return speechReadyMessage || 'Speech modeli baslatilamadi. Sunucu ayarlarini kontrol edin.'
      default:
        return asrStatus === 'error' ? (asrError || 'Canli transcript baglantisinda bir sorun olustu.') : null
    }
  })()
  const videoChipLabel = (() => {
    if (speechReadinessReason === 'startup_failed') return 'Transcript hatasi'
    if (asrStatus === 'connected') return 'Ses aktif'
    if (asrStatus === 'reconnecting') return 'Transcript yeniden baglaniyor'
    if (speechReady === false) return 'Transcript beklemede'
    return 'Mikrofon hazirlaniyor'
  })()
  const transcriptGuidance = (() => {
    if (!isRecording) return null
    if (speechReady === false) return null
    if (asrStatus === 'connected' && audioActivityState === 'awaiting-final') {
      return 'Ses aliniyor. Final satirlar kisa bir duraksamadan sonra gorunur.'
    }
    if (asrStatus === 'connected' && audioActivityState === 'capturing') {
      return 'Mikrofon canli. Normal sekilde konusun; kisa duraksamalar final transcript satirlarini hizlandirir.'
    }
    return null
  })()

  return (
    <div className="page interview-page session-page">
      <div className="session-shell">
        <div className="session-header">
          <div>
            <span className="eyebrow">Canli AI Interview</span>
            <h1 style={{ marginBottom: 0 }}>{session.selectedRole}</h1>
          </div>
          <div className="session-header-meta">
            <span className="status-pill">{isRecording ? 'Canli oturum' : 'Hazir'}</span>
            <span className="session-timer">{formattedElapsed}</span>
          </div>
        </div>

        <div className="interview-layout">
          <div className="question-column">
            <section className="question-card">
              <div className="question-meta">Soru {currentQuestionIndex + 1} / {questions.length || 1}</div>
              {currentQuestion && <h2>{currentQuestion.prompt}</h2>}
              <div className="question-tags">
                <span className="tag">{session.selectedRole || 'Interview'}</span>
                <span className="tag">{session.language === 'en' ? 'English' : 'Turkish'}</span>
                <span className="tag">{isRecording ? 'Live feedback' : 'Waiting to start'}</span>
              </div>
            </section>

            <section className="analysis-card">
              <div className="eyebrow" style={{ marginBottom: 12 }}>AI Assistant Analysis</div>
              <div className="analysis-grid">
                <div className="analysis-mini">
                  <div className="analysis-mini-header">
                    <span>Goz temasi</span>
                    <span>{eyeContactPercent}%</span>
                  </div>
                  <div className="analysis-track">
                    <div className="analysis-fill primary" style={{ width: `${Math.max(0, Math.min(100, eyeContactPercent))}%` }} />
                  </div>
                  <div className="live-transcript-line">Kamera odagi ve bakis hizasi bu kartta ozetlenir.</div>
                </div>

                <div className="analysis-mini">
                  <div className="analysis-mini-header">
                    <span>Stabil anlatim</span>
                    <span>{pacePercent}%</span>
                  </div>
                  <div className="analysis-track">
                    <div className="analysis-fill secondary" style={{ width: `${Math.max(0, Math.min(100, pacePercent))}%` }} />
                  </div>
                  <div className="live-transcript-line">Ses temposu ve kafa hareketi akisinin dengesi.</div>
                </div>
              </div>

              <div className="analysis-mini" style={{ marginTop: 16 }}>
                <div className="analysis-mini-header">
                  <span>Anlik yorum</span>
                  <span>{behaviorStats.dominantEmotion}</span>
                </div>
                <div className="live-transcript-line">{sentimentLabel}</div>
              </div>
            </section>

            {isRecording && (
              <LiveHints
                hints={coaching}
                metrics={currentMetrics}
                behaviorStats={behaviorStats}
                warningHistory={warningHistory}
              />
            )}
          </div>

          <div className="video-column">
            <div className="video-stage-wrap">
              <div className="video-chip">{videoChipLabel}</div>
              <div className="video-badge">AI</div>
              <div className="video-stage">
                <div className="mechanical-clock" title={clockNow.toLocaleTimeString('tr-TR')}>
                  <span className="clock-center" />
                  <span className="clock-hand hour" style={{ transform: `translateX(-50%) rotate(${mechanicalClock.hrDeg}deg)` }} />
                  <span className="clock-hand minute" style={{ transform: `translateX(-50%) rotate(${mechanicalClock.minDeg}deg)` }} />
                </div>
                {isRecording && mediaReady && (
                  <VideoCanvas
                    width={1280}
                    height={720}
                    stream={videoStream}
                    onFrame={handleFrame}
                    drawLandmarks={showOverlay}
                    faceLandmarks={faceLandmarks}
                    poseLandmarks={poseLandmarks}
                    showFaceDetails={showFaceOverlay}
                    showPoseDetails={showPoseOverlay}
                    showDiagnostics={showDiagnosticsOverlay}
                  />
                )}

                {isRecording && !mediaReady && (
                  <div className="camera-placeholder">
                    <p>Vision modeli hazir degil. Audio transcript bu sirada calismaya devam edebilir.</p>
                  </div>
                )}

                {VISION_DEBUG_OVERLAY && isRecording && (
                  <div className="vision-debug-overlay">
                    <div className="vision-debug-title">
                      Vision Metrics ({visionMetrics.calibrated ? 'Calibrated' : 'Calibrating'})
                    </div>
                    {!visionMetrics.calibrated && (
                      <div className="vision-debug-line">remaining: {(calibrationRemainingMs / 1000).toFixed(1)}s</div>
                    )}
                    <div className="vision-debug-line">eyeContact: {visionMetrics.eyeContact.toFixed(2)}</div>
                    <div className="vision-debug-line">posture: {visionMetrics.posture.toFixed(2)}</div>
                    <div className="vision-debug-line">fidget: {visionMetrics.fidget.toFixed(2)}</div>
                    <div className="vision-debug-line">headJitter: {visionMetrics.headJitter.toFixed(2)}</div>
                    <div className="vision-debug-line">eyeOpenness: {visionMetrics.eyeOpenness.toFixed(2)}</div>
                  </div>
                )}

                {DEBUG_TRANSPORT && isRecording && (
                  <div className="transport-debug-overlay">
                    <div className="transport-debug-title">Transport</div>
                    <div className="transport-debug-line">queued: {transportStats.queued}</div>
                    <div className="transport-debug-line">sent: {transportStats.sent}</div>
                    <div className="transport-debug-line">dropped: {transportStats.dropped}</div>
                    <div className="transport-debug-line">failed: {transportStats.failedBatches}</div>
                    <div className="transport-debug-line">error: {transportStats.lastError || '-'}</div>
                  </div>
                )}

                {!isRecording && (
                  <div className="camera-placeholder">
                    <p>Start Recording ile kamera ve mikrofonu baslatin.</p>
                  </div>
                )}
              </div>
            </div>

            <section className="transcript-shell">
              <aside className="live-transcript-panel">
                <div className="live-transcript-header">
                  Transcript updates
                  <span className={`asr-status-badge status-${asrStatus}`}>
                    {asrStatusLabel}
                  </span>
                </div>
                <div className="live-transcript-body">
                  {speechReadyMessage && !asrError && (
                    <div className="live-transcript-notice">
                      {speechReadyMessage}
                    </div>
                  )}
                  {asrNotice && !asrError && asrNotice !== speechReadyMessage && (
                    <div className="live-transcript-notice">
                      {asrNotice}
                    </div>
                  )}
                  {transcriptGuidance && !asrError && (
                    <div className="live-transcript-notice">
                      {transcriptGuidance}
                    </div>
                  )}
                  {sileroFallbackWarning && !asrError && (
                    <div className="live-transcript-notice">
                      {sileroFallbackWarning}
                    </div>
                  )}
                  {asrError && (
                    <div className="live-transcript-empty">
                      {asrError}
                    </div>
                  )}
                  {!asrError && liveTranscriptLines.length === 0 && !liveTranscriptInterim && (
                    <div className="live-transcript-empty">
                      Speak naturally. Finalized transcript lines appear after short pauses.
                    </div>
                  )}
                  {liveTranscriptLines.map((line, idx) => (
                    <div key={`${idx}-${line.slice(0, 12)}`} className="live-transcript-line">
                      {line}
                    </div>
                  ))}
                  {liveTranscriptInterim && (
                    <div className="live-transcript-interim">{liveTranscriptInterim}</div>
                  )}
                  {(isRecording || speechReadyMessage || lastPartialAt || lastFinalAt) && (
                    <div className="live-transcript-diagnostics">
                      <div className="transcript-diag-section">
                        <div className="transcript-diag-title">Bağlantı Durumu</div>
                        <div className="transcript-diag-row">
                          <span className={`diag-dot ${asrStatus === 'connected' ? 'dot-green' : asrStatus === 'error' ? 'dot-red' : 'dot-yellow'}`} />
                          <span>WebSocket: {asrStatusLabel}</span>
                        </div>
                        <div className="transcript-diag-row">
                          <span className={`diag-dot ${speechReady ? 'dot-green' : speechReady === false ? 'dot-red' : 'dot-yellow'}`} />
                          <span>Model: {speechModelLabel}</span>
                        </div>
                        <div className="transcript-diag-row">
                          <span>Ses akışı: {audioActivityLabel}</span>
                        </div>
                      </div>

                      {speechDiagnostics && (
                        <div className="transcript-diag-section">
                          <div className="transcript-diag-title">Sistem Bilgisi</div>
                          <div className="transcript-diag-grid">
                            <div className="diag-stat">
                              <span className="diag-stat-value">{speechDiagnostics.model}</span>
                              <span className="diag-stat-label">Model</span>
                            </div>
                            <div className="diag-stat">
                              <span className="diag-stat-value">{speechDiagnostics.compute_type}</span>
                              <span className="diag-stat-label">Compute</span>
                            </div>
                            <div className="diag-stat">
                              <span className="diag-stat-value">{speechDiagnostics.device}</span>
                              <span className="diag-stat-label">Cihaz</span>
                            </div>
                            <div className="diag-stat">
                              <span className="diag-stat-value">{speechDiagnostics.vad_backend}</span>
                              <span className="diag-stat-label">VAD</span>
                            </div>
                            <div className="diag-stat">
                              <span className="diag-stat-value">{speechDiagnostics.strict_quality_mode ? 'Strict' : 'Relaxed'}</span>
                              <span className="diag-stat-label">Kalite modu</span>
                            </div>
                            <div className="diag-stat">
                              <span className="diag-stat-value">{speechDiagnostics.silero_available ? 'Active' : 'Fallback'}</span>
                              <span className="diag-stat-label">Silero</span>
                            </div>
                            <div className="diag-stat">
                              <span className="diag-stat-value">{speechDiagnostics.audio_input_contract || '—'}</span>
                              <span className="diag-stat-label">Ses kontrati</span>
                            </div>
                            <div className="diag-stat">
                              <span className="diag-stat-value">
                                {speechDiagnostics.live_input_sample_rate > 0
                                  ? `${speechDiagnostics.live_input_sample_rate} Hz / ${speechDiagnostics.live_input_channels} ch`
                                  : '—'}
                              </span>
                              <span className="diag-stat-label">Canli format</span>
                            </div>
                          </div>
                        </div>
                      )}

                      {speechDiagnostics && (
                        <div className="transcript-diag-section">
                          <div className="transcript-diag-title">Performans</div>
                          <div className="transcript-diag-grid">
                            <div className="diag-stat">
                              <span className="diag-stat-value">{speechDiagnostics.avg_transcribe_latency_ms > 0 ? `${Math.round(speechDiagnostics.avg_transcribe_latency_ms)}ms` : '—'}</span>
                              <span className="diag-stat-label">Ort. Gecikme</span>
                            </div>
                            <div className="diag-stat">
                              <span className="diag-stat-value">{speechDiagnostics.p95_transcribe_latency_ms > 0 ? `${Math.round(speechDiagnostics.p95_transcribe_latency_ms)}ms` : '—'}</span>
                              <span className="diag-stat-label">P95 Gecikme</span>
                            </div>
                            <div className="diag-stat">
                              <span className="diag-stat-value">{speechDiagnostics.client_finals_received}</span>
                              <span className="diag-stat-label">Final</span>
                            </div>
                            <div className="diag-stat">
                              <span className="diag-stat-value">{speechDiagnostics.client_partials_received}</span>
                              <span className="diag-stat-label">Partial</span>
                            </div>
                            <div className="diag-stat">
                              <span className="diag-stat-value">
                                {vadChunkTotal > 0 && vadVoicedRatio !== null && vadRejectedRatio !== null
                                  ? `${vadVoicedRatio}% / ${vadRejectedRatio}%`
                                  : '-'}
                              </span>
                              <span className="diag-stat-label">Voiced / Rejected</span>
                            </div>
                            <div className="diag-stat">
                              <span className="diag-stat-value">{speechDiagnostics.filtered_decode_results_total}</span>
                              <span className="diag-stat-label">Filtered decodes</span>
                            </div>
                            <div className="diag-stat">
                              <span className="diag-stat-value">{speechDiagnostics.empty_decode_results_total}</span>
                              <span className="diag-stat-label">Empty decodes</span>
                            </div>
                            <div className="diag-stat">
                              <span className="diag-stat-value">{speechDiagnostics.duplicate_finals_suppressed_total}</span>
                              <span className="diag-stat-label">Suppressed dupes</span>
                            </div>
                            <div className="diag-stat">
                              <span className="diag-stat-value">{uploadFormatLabel}</span>
                              <span className="diag-stat-label">Son upload</span>
                            </div>
                            <div className="diag-stat">
                              <span className="diag-stat-value">{uploadSampleLabel}</span>
                              <span className="diag-stat-label">Upload format</span>
                            </div>
                          </div>
                        </div>
                      )}

                      <div className="transcript-diag-section">
                        <div className="transcript-diag-title">Zaman Çizelgesi</div>
                        <div className="transcript-diag-row">
                          <span>Son partial: {lastPartialLabel}</span>
                        </div>
                        <div className="transcript-diag-row">
                          <span>Son final: {lastFinalLabel}</span>
                        </div>
                      </div>

                      {speechIssueDetail && (
                        <div className="diag-error-banner">
                          <div className="diag-error-title">⚠ Sorun Tespit Edildi</div>
                          <div className="diag-error-detail">
                            {!speechReadyMessage || speechReadyMessage.includes('ulasilamiyor')
                              ? 'Speech servisi konteynerine erişilemiyor. Docker servisleri çalışıyor mu kontrol edin.'
                              : speechReadyMessage.includes('yukleniyor') || speechReadyMessage.includes('isiniyor')
                                ? 'Model ilk defa yükleniyor veya ısınıyor. Bu işlem 1-2 dakika sürebilir.'
                                : speechReadyMessage.includes('dolu')
                                  ? 'Tüm oturum slotları dolu. Başka bir tarayıcı sekmesinde açık mülakat varsa kapatın.'
                                  : speechReadyMessage || 'Bilinmeyen hata. Logları kontrol edin.'}
                          </div>
                        </div>
                      )}

                      {isRecording && asrStatus === 'connected' && audioActivityState === 'capturing' && !lastPartialAt && (
                        <div className="diag-info-banner">
                          Mikrofon aktif. Ses alınıyor ancak henüz konuşma algılanmadı. Daha net ve yüksek sesle konuşmayı deneyin.
                        </div>
                      )}
                    </div>
                  )}
                </div>
              </aside>

              <div>
                <div className="controls">
                  <button
                    onClick={isRecording ? stopRecording : startRecording}
                    className={`btn ${isRecording ? 'btn-danger' : 'btn-primary'}`}
                  >
                    {isRecording ? 'Kaydi Durdur' : 'Start Recording'}
                  </button>
                  <button
                    type="button"
                    onClick={() => setShowOverlay(v => !v)}
                    className="btn btn-secondary"
                  >
                    {showOverlay ? 'Overlay On' : 'Overlay Off'}
                  </button>
                </div>

                <div className="overlay-controls" style={{ marginTop: 12 }}>
                  <button
                    type="button"
                    onClick={() => setShowFaceOverlay(v => !v)}
                    className="btn btn-secondary btn-sm"
                    disabled={!showOverlay}
                  >
                    {showFaceOverlay ? 'Face details on' : 'Face details off'}
                  </button>
                  <button
                    type="button"
                    onClick={() => setShowPoseOverlay(v => !v)}
                    className="btn btn-secondary btn-sm"
                    disabled={!showOverlay}
                  >
                    {showPoseOverlay ? 'Body details on' : 'Body details off'}
                  </button>
                  <button
                    type="button"
                    onClick={() => setShowDiagnosticsOverlay(v => !v)}
                    className="btn btn-secondary btn-sm"
                    disabled={!showOverlay}
                  >
                    {showDiagnosticsOverlay ? 'Live stats on' : 'Live stats off'}
                  </button>
                </div>

                {showOverlay && (
                  <p className="overlay-info" style={{ marginTop: 14 }}>
                    Overlay acik: yuz mesh, pose ve canli landmark tanilama gorunuyor.
                  </p>
                )}

                {llmInsight && (
                  <div className="llm-insight" style={{ marginTop: 18 }}>
                    <div className="llm-insight-title">
                      LLM Canli Analiz ({llmInsight.model}) - Guven: %{Math.round((llmInsight.confidence || 0) * 100)}
                    </div>
                    <div className="llm-insight-summary">{llmInsight.summary}</div>
                    {llmInsight.risks?.length > 0 && (
                      <div className="llm-insight-list">
                        Riskler: {llmInsight.risks.join(' | ')}
                      </div>
                    )}
                    {llmInsight.suggestions?.length > 0 && (
                      <div className="llm-insight-list">
                        Oneriler: {llmInsight.suggestions.join(' | ')}
                      </div>
                    )}
                  </div>
                )}
              </div>
            </section>

            <div className="nav-buttons">
              <button onClick={handleNext} disabled={uploading} className="btn btn-primary">
                {uploading ? 'Processing...' : currentQuestionIndex < questions.length - 1 ? 'Next Question' : 'Finish'}
              </button>
              {backgroundTranscribes > 0 && (
                <p className="subtitle" style={{ marginBottom: 0 }}>Onceki cevaplar arka planda isleniyor...</p>
              )}
            </div>
          </div>
        </div>
      </div>

      <TranscriptModal
        isOpen={showTranscript}
        transcript={transcriptData?.full_text || ''}
        segments={transcriptData?.segments || []}
        stats={transcriptData ? {
          duration_ms: transcriptData.duration_ms,
          word_count: transcriptData.word_count,
          wpm: transcriptData.wpm,
          filler_count: transcriptData.filler_count,
          filler_words: transcriptData.filler_words,
          pause_count: transcriptData.pause_count,
          average_pause_ms: transcriptData.average_pause_ms
        } : null}
        onClose={() => {
          setShowTranscript(false)
          proceedToNext()
        }}
      />
    </div>
  )
}

export default InterviewSession
