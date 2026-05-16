import { useState, useEffect, useRef } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import ApiService, { LiveAnalysisResponse } from '../services/ApiService'
import { initMediaPipe, detectLandmarks, isMediaPipeReady } from '../services/MediaPipeService'
import { MetricsComputer, Metrics, BehaviorStats } from '../services/MetricsComputer'
import { AudioAnalyzer } from '../services/AudioAnalyzer'
import { SessionTransport, SessionTransportStats } from '../services/SessionTransport'
import { FixedRollingBuffer } from '../vision/buffer'
import {
  computeEyeContactScore,
  computeEyeOpenness,
  computeFidgetScore,
  computeHeadJitter,
  computeHeadPose,
  computePostureScore,
  smooth,
  parseBlendshapes,
  computeDerivedSignals
} from '../vision/features'
import type { DerivedSignals } from '../vision/types'
import { Calibration, HeadPose, PoseLandmarks, VisionMetrics } from '../vision/types'
import { VideoCanvas } from '../components/VideoCanvas'
import * as RecordingBuffer from '../services/RecordingBuffer'
import '../styles/pages.css'

const VISION_DEBUG_OVERLAY = import.meta.env.DEV
const DEBUG_TRANSPORT = import.meta.env.DEV
const CALIBRATION_DURATION_MS = 5000
const VISION_EVENT_INTERVAL_MS = 500
const ROLLING_SECONDS = 5
const APPROX_FPS = 30
const VISION_BUFFER_SIZE = ROLLING_SECONDS * APPROX_FPS
const METRIC_SMOOTH_ALPHA = 0.2

function normalizeTranscriptText(text: string): string {
  return text.trim().toLocaleLowerCase('tr-TR').replace(/\s+/g, ' ')
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
  const [uploading, setUploading] = useState(false)
  const [videoStream, setVideoStream] = useState<MediaStream | null>(null)
  const [showOverlay, setShowOverlay] = useState(true)
  const [showFaceOverlay, setShowFaceOverlay] = useState(true)
  const [showPoseOverlay, setShowPoseOverlay] = useState(true)
  const [showDiagnosticsOverlay, setShowDiagnosticsOverlay] = useState(true)
  const [faceLandmarks, setFaceLandmarks] = useState<any[] | undefined>(undefined)
  const [poseLandmarks, setPoseLandmarks] = useState<any[] | undefined>(undefined)
  const [llmInsight, setLlmInsight] = useState<LiveAnalysisResponse | null>(null)
  const [derivedSignals, setDerivedSignals] = useState<DerivedSignals>({
    smileIntensity: 0, isDuchenne: false, lipPress: 0,
    browFurrow: 0, jawTension: 0, arousalIndex: 0,
    stressSignal: 0, discomfortSignal: 0
  })
  const [speechReady, setSpeechReady] = useState<boolean | null>(null)
  const [clockNow, setClockNow] = useState(new Date())
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
  const [pendingUploads, setPendingUploads] = useState(0)
  const [pendingRetryCount, setPendingRetryCount] = useState(0)
  const [isReconnecting, setIsReconnecting] = useState(false)
  const [isFinalizePending, setIsFinalizePending] = useState(false)
  const [hasRecordedCurrentQuestion, setHasRecordedCurrentQuestion] = useState(false)

  const pendingUploadsRef = useRef(0)
  const autoRestartInProgressRef = useRef(false)
  // Tracks the 1-based question order that the ACTIVE recording belongs to.
  // Set when recording starts, read in handleNext() to guard against order mismatches.
  const activeRecordingQuestionOrderRef = useRef<number>(0)
  // Sequential transcription chain — ensures Q2 transcription only starts after Q1 finishes.
  // Prevents concurrent Whisper GPU inference that silently drops later requests.
  const transcriptionChainRef = useRef<Promise<void>>(Promise.resolve())
  const metricsComputerRef = useRef<MetricsComputer | null>(null)
  const audioAnalyzerRef = useRef<AudioAnalyzer | null>(null)
  const mediaRecorderRef = useRef<MediaRecorder | null>(null)
  const audioChunksRef = useRef<Blob[]>([])
  const recordingMimeTypeRef = useRef('video/webm')
  // Screen recording
  const screenStreamRef = useRef<MediaStream | null>(null)
  const screenRecorderRef = useRef<MediaRecorder | null>(null)
  const screenChunksRef = useRef<Blob[]>([])
  const screenMimeTypeRef = useRef('video/webm')
  // Session-level timing (set once, never reset between questions)
  const trueSessionStartMsRef = useRef<number | null>(null)
  const questionStartMsRef = useRef<number>(0)
  const questionEndMsRef = useRef<number>(0)
  const frameCountRef = useRef(0)
  const mediaStreamRef = useRef<MediaStream | null>(null)
  const lastPoseLandmarksRef = useRef<any[] | null>(null)
  const poseMissFramesRef = useRef(0)
  const liveAnalysisIntervalRef = useRef<ReturnType<typeof setInterval> | null>(null)
  const metricsWindowRef = useRef<Metrics[]>([])
  const behaviorStatsRef = useRef<BehaviorStats>(behaviorStats)
  const isRecordingRef = useRef(false)
  const sessionTransportRef = useRef<SessionTransport | null>(null)
  const transportStatsIntervalRef = useRef<ReturnType<typeof setInterval> | null>(null)
  const sessionStartAtMsRef = useRef<number | null>(null)
  const adaptiveQuestionsRequestedRef = useRef(false)
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

    const checkSpeechService = async () => {
      try {
        const speechUrl = import.meta.env.VITE_SPEECH_URL || 'http://localhost:8000'
        const resp = await fetch(`${speechUrl}/health`)
        setSpeechReady(resp.ok)
      } catch {
        setSpeechReady(false)
      }
    }
    void checkSpeechService()
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

  // When uploads drain and finalize was deferred, run it now
  useEffect(() => {
    if (isFinalizePending && pendingUploads === 0) {
      setIsFinalizePending(false)
      void doFinalize()
    }
  }, [pendingUploads, isFinalizePending])

  // On mount: retry any pending recordings for this session left from a previous load
  useEffect(() => {
    if (!sessionId) return
    void (async () => {
      try {
        const pending = (await RecordingBuffer.getPendingRecordings())
          .filter(e => e.sessionId === sessionId)
        for (const entry of pending) {
          try {
            if (entry.key.startsWith('screen:')) {
              await ApiService.uploadQuestionScreen(entry.sessionId, entry.questionOrder, entry.blob, entry.mimeType)
            } else {
              await ApiService.uploadQuestionAudio(entry.sessionId, entry.questionOrder, entry.blob, entry.mimeType, entry.startMs, entry.endMs)
            }
            await RecordingBuffer.removeRecording(entry.key)
          } catch {
            // Will retry on next load
          }
        }
      } catch {
        // IndexedDB unavailable
      }
    })()
  }, [sessionId])

  // Background upload retry: every 30s re-attempt any IndexedDB-pending recordings
  useEffect(() => {
    if (!sessionId) return
    const id = setInterval(async () => {
      try {
        const pending = (await RecordingBuffer.getPendingRecordings())
          .filter(e => e.sessionId === sessionId)
        for (const entry of pending) {
          try {
            if (entry.key.startsWith('screen:')) {
              await ApiService.uploadQuestionScreen(entry.sessionId, entry.questionOrder, entry.blob, entry.mimeType)
            } else {
              await ApiService.uploadQuestionAudio(entry.sessionId, entry.questionOrder, entry.blob, entry.mimeType, entry.startMs, entry.endMs)
            }
            await RecordingBuffer.removeRecording(entry.key)
            setPendingRetryCount(prev => Math.max(0, prev - 1))
          } catch { /* will try again next interval */ }
        }
      } catch { /* IndexedDB unavailable */ }
    }, 30_000)
    return () => clearInterval(id)
  }, [sessionId])

  // When Q4 starts recording, generate adaptive questions Q5–Q8 in background
  useEffect(() => {
    if (
      currentQuestionIndex === 3 &&
      isRecording &&
      sessionId &&
      !adaptiveQuestionsRequestedRef.current
    ) {
      adaptiveQuestionsRequestedRef.current = true
      void (async () => {
        try {
          const newQs = await ApiService.generateAdaptiveQuestions(sessionId)
          if (newQs.length > 0) {
            setQuestions(prev => [...prev, ...newQs])
          }
        } catch {
          // non-fatal: interview continues with existing 4 questions
        }
      })()
    }
  }, [currentQuestionIndex, isRecording, sessionId])

  useEffect(() => {
    return () => {
      closeMediaCapture()
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


  const startRecording = async () => {
    try {
      // Reset chunk buffers so Q2+ recordings start clean
      audioChunksRef.current = []
      screenChunksRef.current = []

      resetVisionPipeline()
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

      // True session start — set only once for session-relative MetricEvent timestamps
      if (trueSessionStartMsRef.current === null) {
        trueSessionStartMsRef.current = performance.now()
      }
      questionStartMsRef.current = Math.round(performance.now() - trueSessionStartMsRef.current)

      // Request screen share permission for every question.
      // We request audio:false from getDisplayMedia (system audio is optional/unreliable),
      // then manually add the microphone track so the screen recording always has the speaker's voice.
      try {
        const screenStream = await navigator.mediaDevices.getDisplayMedia({
          video: { frameRate: { ideal: 15 } },
          audio: false
        })
        // Merge screen video + mic audio into one stream for the screen recorder
        const micAudioTracks = stream.getAudioTracks()
        const combinedScreenStream = new MediaStream([
          ...screenStream.getVideoTracks(),
          ...micAudioTracks
        ])
        screenStreamRef.current = combinedScreenStream
        // When the user stops screen share, clear the ref
        screenStream.getVideoTracks()[0]?.addEventListener('ended', () => {
          screenStreamRef.current = null
          screenRecorderRef.current = null
        })
      } catch {
        // Permission denied — continue with webcam only
        screenStreamRef.current = null
      }

      try {
        if (typeof MediaRecorder !== 'undefined') {
          const mimeCandidates = [
            'video/webm;codecs=vp8,opus',
            'video/webm',
            'video/mp4'
          ]
          const supportedMimeType = mimeCandidates.find(type => MediaRecorder.isTypeSupported(type))
          mediaRecorderRef.current = supportedMimeType
            ? new MediaRecorder(stream, { mimeType: supportedMimeType })
            : new MediaRecorder(stream)
          recordingMimeTypeRef.current = mediaRecorderRef.current.mimeType || supportedMimeType || 'video/webm'

          mediaRecorderRef.current.ondataavailable = (e) => {
            if (e.data.size > 0) {
              audioChunksRef.current.push(e.data)
            }
          }
          mediaRecorderRef.current.onerror = () => {
            if (isRecordingRef.current) void autoRestartRecording()
          }
          stream.getAudioTracks().forEach(track => {
            track.addEventListener('ended', () => {
              if (isRecordingRef.current) void autoRestartRecording()
            })
          })
          mediaRecorderRef.current.start(1000)

          // Start screen recorder if stream is available (combined: screen video + mic audio)
          if (screenStreamRef.current) {
            try {
              screenChunksRef.current = []
              // Prefer a codec that supports both video and audio
              const screenMimeCandidates = [
                'video/webm;codecs=vp8,opus',
                'video/webm;codecs=vp9,opus',
                'video/webm',
              ]
              const screenMime = screenMimeCandidates.find(type => MediaRecorder.isTypeSupported(type)) ?? 'video/webm'
              screenMimeTypeRef.current = screenMime
              const screenRec = new MediaRecorder(screenStreamRef.current, { mimeType: screenMime })
              screenRec.ondataavailable = (e) => {
                if (e.data.size > 0) screenChunksRef.current.push(e.data)
              }
              screenRec.start(1000)
              screenRecorderRef.current = screenRec
            } catch {
              screenRecorderRef.current = null
            }
          }
        } else {
          console.warn('MediaRecorder unavailable; continuing with live metrics only.')
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

      setIsRecording(true)
      isRecordingRef.current = true
      setHasRecordedCurrentQuestion(true)
      activeRecordingQuestionOrderRef.current = currentQuestionIndex + 1
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
    const transportStopPromise = sessionTransportRef.current?.stop({ flush: true }) ?? Promise.resolve()
    sessionTransportRef.current = null
    if (transportStatsIntervalRef.current) {
      clearInterval(transportStatsIntervalRef.current)
      transportStatsIntervalRef.current = null
    }

    // Record question end time (session-relative)
    if (trueSessionStartMsRef.current !== null) {
      questionEndMsRef.current = Math.round(performance.now() - trueSessionStartMsRef.current)
    }

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

    // Stop screen recorder
    if (screenRecorderRef.current && screenRecorderRef.current.state !== 'inactive') {
      try {
        screenRecorderRef.current.requestData()
      } catch { /* no-op */ }
      screenRecorderRef.current.stop()
    }
    screenRecorderRef.current = null

    // Wait for recorders to fully stop and flush final chunks BEFORE closing tracks.
    // Closing tracks first can prevent the final ondataavailable from firing in some browsers.
    await recorderStopPromise
    mediaRecorderRef.current = null

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
  }

  const handleFrame = async (video: HTMLVideoElement, _canvas: HTMLCanvasElement) => {
    if (!isRecording || !metricsComputerRef.current || !isMediaPipeReady()) {
      return
    }

    frameCountRef.current++

    try {
      const landmarks = await detectLandmarks(video, Date.now())
      const faceLm = landmarks.face?.faceLandmarks?.[0] || []
      // Parse blendshapes and derived signals (requires outputFaceBlendshapes: true)
      const blendshapes = parseBlendshapes(landmarks.face?.faceBlendshapes)
      const derived = computeDerivedSignals(blendshapes)
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
      // Session-relative elapsed (for MetricEvent timestamps — consistent across questions)
      const sessionElapsedMs = trueSessionStartMsRef.current != null
        ? Math.max(0, Math.round(nowMs - trueSessionStartMsRef.current))
        : elapsedMs
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

      // Update derived signals state every frame (blendshape-based)
      if (faceLm.length > 0) {
        setDerivedSignals(derived)
      }

      if (calibrated && sessionElapsedMs - lastVisionEventSentAtRef.current >= VISION_EVENT_INTERVAL_MS) {
        lastVisionEventSentAtRef.current = sessionElapsedMs
        sessionTransportRef.current?.enqueueEvent({
          clientEventId: crypto.randomUUID(),
          tsMs: sessionElapsedMs,
          source: 'Vision',
          type: 'vision_metrics_v1',
          payload: {
            eyeContact: smoothed.eyeContact,
            posture: smoothed.posture,
            fidget: smoothed.fidget,
            headJitter: smoothed.headJitter,
            eyeOpenness: smoothed.eyeOpenness,
            calibrated: true,
            // Blendshape-derived signals
            arousal: Math.round(derived.arousalIndex * 100),
            stress: Math.round(derived.stressSignal * 100),
            isDuchenne: derived.isDuchenne,
            smile: Math.round(derived.smileIntensity * 100)
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
    stopTracks(screenStreamRef.current)

    mediaStreamRef.current = null
    screenStreamRef.current = null
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


  const autoRestartRecording = async () => {
    if (autoRestartInProgressRef.current) return
    autoRestartInProgressRef.current = true
    setIsReconnecting(true)

    // Stop old recorder without clearing chunks — preserve what was captured so far
    try { mediaRecorderRef.current?.stop() } catch { /* no-op */ }
    mediaRecorderRef.current = null
    closeMediaCapture()

    await new Promise(r => setTimeout(r, 1000))

    try {
      const stream = await navigator.mediaDevices.getUserMedia({
        audio: true,
        video: { width: { ideal: 1280 }, height: { ideal: 720 } }
      })
      mediaStreamRef.current = stream
      setVideoStream(stream)

      const mimeCandidates = ['video/webm;codecs=vp8,opus', 'video/webm', 'video/mp4']
      const supportedMimeType = mimeCandidates.find(type => MediaRecorder.isTypeSupported(type))
      const recorder = supportedMimeType
        ? new MediaRecorder(stream, { mimeType: supportedMimeType })
        : new MediaRecorder(stream)
      recordingMimeTypeRef.current = recorder.mimeType || supportedMimeType || 'video/webm'

      recorder.ondataavailable = (e) => {
        if (e.data.size > 0) audioChunksRef.current.push(e.data)
      }
      recorder.onerror = () => {
        if (isRecordingRef.current) void autoRestartRecording()
      }
      stream.getAudioTracks().forEach(track => {
        track.addEventListener('ended', () => {
          if (isRecordingRef.current) void autoRestartRecording()
        })
      })

      recorder.start(1000)
      mediaRecorderRef.current = recorder
      activeRecordingQuestionOrderRef.current = currentQuestionIndex + 1

      try {
        audioAnalyzerRef.current?.dispose?.()
        audioAnalyzerRef.current = new AudioAnalyzer(stream)
      } catch { /* no-op */ }
    } catch {
      // getUserMedia failed — recording truly stopped
      setIsRecording(false)
      isRecordingRef.current = false
    }

    autoRestartInProgressRef.current = false
    setIsReconnecting(false)
  }

  const uploadAndTranscribe = async (chunks: Blob[], _showModal: boolean, questionOrder: number): Promise<boolean> => {
    if (!sessionId || chunks.length === 0) return false

    setUploading(true)
    try {
      const recordingMimeType = recordingMimeTypeRef.current || 'video/webm'
      const audioBlob = new Blob(chunks, { type: recordingMimeType })
      const ext = recordingMimeType.includes('mp4') ? 'mp4' : 'webm'
      const formData = new FormData()
      formData.append('file', audioBlob, `answer.${ext}`)

      // Retry up to 2 times: if the speech service is briefly busy (e.g. still processing the
      // previous question), wait 12 s and try again before giving up.
      let transcriptResult: any = null
      for (let attempt = 0; attempt < 3; attempt++) {
        try {
          // Use 'auto' so Whisper detects the spoken language instead of locking to the session language.
          transcriptResult = await ApiService.transcribeAudio(formData, 'auto')
          break
        } catch (err) {
          if (attempt < 2) {
            console.warn(`Transcription attempt ${attempt + 1} failed for Q${questionOrder}, retrying in 12s…`, err)
            await new Promise(r => setTimeout(r, 12_000))
          } else {
            throw err
          }
        }
      }

      if (transcriptResult.segments?.length > 0 && sessionId) {
        const segments = transcriptResult.segments
          .filter((s: any) => s.text?.trim())
          .map((s: any) => ({
            clientSegmentId: buildDeterministicTranscriptSegmentId(
              Math.max(0, Math.round(s.start_ms ?? 0)),
              Math.max(0, Math.round(s.end_ms ?? 0)),
              s.text.trim()
            ),
            startMs: Math.max(0, Math.round(s.start_ms ?? 0)),
            endMs: Math.max(0, Math.round(s.end_ms ?? 0)),
            text: s.text.trim(),
            confidence: s.confidence,
            questionOrder
          }))

        if (segments.length > 0) {
          await ApiService.postTranscriptBatch(sessionId, segments).catch((err) => {
            console.warn('Transcript batch upload failed:', err)
          })
        }
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
    if (isRecording) {
      await stopRecording()
    }

    const recordedChunks = [...audioChunksRef.current]
    audioChunksRef.current = []
    const questionOrder = currentQuestionIndex + 1 // 1-based

    // Guard: if the recorded chunks belong to a different question than expected, discard them
    // to prevent audio from one question appearing under another.
    const recordedFor = activeRecordingQuestionOrderRef.current
    if (recordedChunks.length > 0 && recordedFor !== 0 && recordedFor !== questionOrder) {
      console.warn(`Audio order mismatch: chunks recorded for Q${recordedFor} but current question is Q${questionOrder}. Discarding.`)
      recordedChunks.length = 0
    }
    activeRecordingQuestionOrderRef.current = 0

    if (recordedChunks.length === 0) {
      const isLastQuestion = currentQuestionIndex >= questions.length - 1
      const label = isLastQuestion ? 'finish the interview' : 'move to the next question'
      const confirmed = window.confirm(
        `No video recording found for Question ${questionOrder}.\nAre you sure you want to ${label} without recording?`
      )
      if (!confirmed) return
    }

    const startMs = questionStartMsRef.current
    const endMs = questionEndMsRef.current

    if (recordedChunks.length > 0 && sessionId) {
      const mimeType = recordingMimeTypeRef.current || 'video/webm'
      const audioBlob = new Blob(recordedChunks, { type: mimeType })
      const bufferKey = `audio:${sessionId}:${questionOrder}`

      void RecordingBuffer.saveRecording({ key: bufferKey, sessionId, questionOrder, blob: audioBlob, mimeType, startMs, endMs })

      incPending()
      void ApiService.uploadQuestionAudio(sessionId, questionOrder, audioBlob, mimeType, startMs, endMs)
        .then(() => void RecordingBuffer.removeRecording(bufferKey))
        .catch(err => {
          console.error(`Audio upload for Q${questionOrder} failed after retries:`, err)
          setPendingRetryCount(n => n + 1)
        })
        .finally(decPending)

      // Chain transcription requests sequentially so Q2 never starts while Q1 is still running.
      // Concurrent Whisper GPU calls silently fail; the chain ensures only one runs at a time.
      const chunksSnapshot = [...recordedChunks]
      transcriptionChainRef.current = transcriptionChainRef.current
        .then(async () => { await uploadAndTranscribe(chunksSnapshot, false, questionOrder) })
        .catch(() => { /* non-fatal — transcript missing for this question */ })
    }

    // Upload screen recording if available
    const screenChunks = [...screenChunksRef.current]
    screenChunksRef.current = []
    if (screenChunks.length > 0 && sessionId) {
      const screenMime = screenMimeTypeRef.current || 'video/webm'
      const screenBlob = new Blob(screenChunks, { type: screenMime })
      const screenKey = `screen:${sessionId}:${questionOrder}`

      void RecordingBuffer.saveRecording({ key: screenKey, sessionId, questionOrder, blob: screenBlob, mimeType: screenMime })

      incPending()
      void ApiService.uploadQuestionScreen(sessionId, questionOrder, screenBlob, screenMime)
        .then(() => void RecordingBuffer.removeRecording(screenKey))
        .catch(() => { /* screen failure non-fatal; blob stays in IndexedDB for retry */ })
        .finally(decPending)
    }

    proceedToNext()
  }


  const proceedToNext = () => {
    if (currentQuestionIndex < questions.length - 1) {
      setCurrentQuestionIndex(currentQuestionIndex + 1)
      audioChunksRef.current = []
      setHasRecordedCurrentQuestion(false)
    } else {
      finalize()
    }
  }

  const incPending = () => {
    pendingUploadsRef.current += 1
    setPendingUploads(pendingUploadsRef.current)
  }

  const decPending = () => {
    pendingUploadsRef.current = Math.max(0, pendingUploadsRef.current - 1)
    setPendingUploads(pendingUploadsRef.current)
  }

  const doFinalize = async () => {
    if (!sessionId) return
    try {
      await ApiService.finalizeSession(sessionId)
    } catch (error) {
      console.error('Finalize failed, navigating to report anyway:', error)
    }
    navigate(`/report/${sessionId}`)
  }

  const finalize = async () => {
    if (pendingUploadsRef.current > 0) {
      setIsFinalizePending(true)
      return
    }
    await doFinalize()
  }

  const handleAbortClick = () => {
    if (pendingUploadsRef.current > 0) {
      const go = window.confirm(
        `${pendingUploadsRef.current} recording(s) are still uploading. Leaving now will lose this data. Continue?`
      )
      if (!go) return
    } else {
      const confirmed = window.confirm(
        'Are you sure you want to stop the interview? Unsaved progress will be lost.'
      )
      if (!confirmed) return
    }
    void stopRecording()
    navigate('/')
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
  const sentimentLabel = llmInsight?.summary || `${behaviorStats.currentEmotion} delivery`

  return (
    <div className="page interview-page session-page">
      <div className="session-shell">
        <div className="session-header">
          {/* Role title */}
          <div className="session-header-role">
            <span className="eyebrow">Live AI Interview</span>
            <span className="session-role-name">{session.selectedRole}</span>
          </div>

          {/* Record button + readiness */}
          <div className="action-bar-section action-bar-section--main">
            {!isRecording && (
              <span className="action-bar-ready">
                {!mediaReady
                  ? '⏳ Loading model...'
                  : speechReady === null
                    ? '⏳ Checking service...'
                    : '✅ Ready'}
              </span>
            )}
            <button
              onClick={isRecording ? stopRecording : startRecording}
              disabled={!isRecording && (!mediaReady || speechReady === null)}
              className={`btn ${isRecording ? 'btn-danger' : 'btn-primary'}`}
            >
              {isRecording
                ? '⏹ Stop Recording'
                : !mediaReady
                  ? 'Loading Model...'
                  : speechReady === null
                    ? 'Checking Service...'
                    : '⏺ Start Recording'}
            </button>
          </div>

          {/* Service status + live metrics */}
          <div className="action-bar-section action-bar-section--status">
            <div className="status-row">
              <span className="status-label">Video</span>
              <span className={`status-dot ${mediaReady ? 'dot-ok' : 'dot-loading'}`}>
                {mediaReady ? '● Active' : '◌ Loading'}
              </span>
            </div>
            <div className="status-row">
              <span className="status-label">Audio</span>
              <span className={`status-dot ${speechReady ? 'dot-ok' : 'dot-warn'}`}>
                {speechReady ? '● Ready' : '◌ Waiting'}
              </span>
            </div>
            {isRecording && (
              <>
                <div className="status-row">
                  <span className="status-label">Eye Contact</span>
                  <span className="status-value-sm">{eyeContactPercent}%</span>
                </div>
                <div className="status-row">
                  <span className="status-label">Posture</span>
                  <span className="status-value-sm">{Math.round(currentMetrics.posture)}%</span>
                </div>
              </>
            )}
          </div>

          {/* Timer + navigation + stop */}
          <div className="session-header-meta">
            <span className="session-timer">{formattedElapsed}</span>
            <button onClick={handleNext} disabled={uploading || isFinalizePending} className="btn btn-primary btn-sm">
              {isFinalizePending
                ? `Saving... (${pendingUploads})`
                : uploading
                  ? 'Processing...'
                  : currentQuestionIndex < questions.length - 1
                    ? 'Next Question →'
                    : 'Finish Interview ✓'}
            </button>
            <button
              type="button"
              className="btn btn-sm btn-danger"
              onClick={handleAbortClick}
            >
              ✕ Stop
            </button>
          </div>
        </div>

        {isReconnecting && (
          <div className="upload-error-banner upload-error-banner--reconnecting">
            Reconnecting microphone...
          </div>
        )}
        {!isReconnecting && pendingRetryCount > 0 && (
          <div className="upload-error-banner upload-error-banner--pending">
            Upload pending — retrying automatically ({pendingRetryCount} file{pendingRetryCount > 1 ? 's' : ''})
          </div>
        )}

        {/* AI live insight (shown when available) */}
        {llmInsight && (
          <div className="action-bar-insight">
            <strong>AI Insight</strong> — {llmInsight.summary}
            {llmInsight.risks?.length > 0 && <span> · ⚠ {llmInsight.risks[0]}</span>}
            {llmInsight.suggestions?.length > 0 && <span> · 💡 {llmInsight.suggestions[0]}</span>}
          </div>
        )}

        <div className="interview-layout">
          <div className="question-column">
            <section className={`question-card${!isRecording && !hasRecordedCurrentQuestion ? ' question-card--unrecorded' : ''}`}>
              <div className="question-meta">Question {currentQuestionIndex + 1} / {questions.length || 1}</div>
              {currentQuestion && <h2>{currentQuestion.prompt}</h2>}
              <div className="question-tags">
                <span className="tag">{session.selectedRole || 'Interview'}</span>
                <span className="tag">{session.language === 'en' ? 'English' : 'Turkish'}</span>
                <span className="tag">{isRecording ? 'Live Feedback' : 'Waiting to Start'}</span>
              </div>
              {!isRecording && !hasRecordedCurrentQuestion && (
                <div className="question-no-recording-notice">
                  ⚠ Recording not started — click Start Recording before answering
                </div>
              )}
            </section>
          </div>

          {/* ── ORTA: Video ── */}
          <div className="video-column">
            <div className="video-stage-wrap">
              <div className="video-chip">Recording active</div>
              <div className="video-badge">AI</div>
              <div className="video-stage">
                <div className="mechanical-clock" title={clockNow.toLocaleTimeString('en-US')}>
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
                    <p>Vision model not ready. Audio recording can continue in the meantime.</p>
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
                    <p>Click Start Recording to activate camera and microphone.</p>
                  </div>
                )}
              </div>
            </div>

            {/* Overlay controls — directly below video */}
            <div className="video-overlay-controls">
              <button
                type="button"
                onClick={() => setShowOverlay(v => !v)}
                className="btn btn-secondary btn-sm"
              >
                {showOverlay ? 'Hide Overlay' : 'Show Overlay'}
              </button>
              {showOverlay && (
                <>
                  <button type="button" onClick={() => setShowFaceOverlay(v => !v)} className="btn btn-secondary btn-sm">
                    {showFaceOverlay ? 'Face ✓' : 'Face'}
                  </button>
                  <button type="button" onClick={() => setShowPoseOverlay(v => !v)} className="btn btn-secondary btn-sm">
                    {showPoseOverlay ? 'Body ✓' : 'Body'}
                  </button>
                </>
              )}
            </div>
          </div>

        </div>

        {/* ── AI ANALYSIS — full width ── */}
        <section className="analysis-card analysis-card--wide">
          <div className="eyebrow" style={{ marginBottom: 12 }}>AI Assistant Analysis</div>
          <div className="analysis-grid analysis-grid--3col">
            <div className="analysis-mini">
              <div className="analysis-mini-header">
                <span>Eye Contact</span>
                <span>{eyeContactPercent}%</span>
              </div>
              <div className="analysis-track">
                <div className="analysis-fill primary" style={{ width: `${Math.max(0, Math.min(100, eyeContactPercent))}%` }} />
              </div>
              <div className="live-transcript-line">Camera focus and gaze direction are summarized here.</div>
            </div>

            <div className="analysis-mini">
              <div className="analysis-mini-header">
                <span>Stable Delivery</span>
                <span>{pacePercent}%</span>
              </div>
              <div className="analysis-track">
                <div className="analysis-fill secondary" style={{ width: `${Math.max(0, Math.min(100, pacePercent))}%` }} />
              </div>
              <div className="live-transcript-line">Balance of speech tempo and head movement.</div>
            </div>

            <div className="analysis-mini">
              <div className="analysis-mini-header">
                <span>Live Insight</span>
                <span>{behaviorStats.dominantEmotion}</span>
              </div>
              <div className="live-transcript-line">{sentimentLabel}</div>
            </div>
          </div>
        </section>

      </div>


    </div>
  )
}

export default InterviewSession
