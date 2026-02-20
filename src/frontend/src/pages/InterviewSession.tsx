import { useState, useEffect, useRef } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import ApiService, { LiveAnalysisResponse } from '../services/ApiService'
import { initMediaPipe, detectLandmarks, isMediaPipeReady } from '../services/MediaPipeService'
import { MetricsComputer, Metrics, BehaviorStats } from '../services/MetricsComputer'
import { generateCoachingHints, CoachingHint } from '../services/CoachingHints'
import { AudioAnalyzer } from '../services/AudioAnalyzer'
import { VideoCanvas } from '../components/VideoCanvas'
import { LiveHints } from '../components/LiveHints'
import { TranscriptModal } from '../components/TranscriptModal'
import '../styles/pages.css'

interface WarningHistoryItem {
  id: string
  type: 'warning' | 'info'
  message: string
  time: string
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
  const [liveTranscriptSupported, setLiveTranscriptSupported] = useState(true)
  const [clockNow, setClockNow] = useState(new Date())
  const [warningHistory, setWarningHistory] = useState<WarningHistoryItem[]>([])

  const metricsComputerRef = useRef<MetricsComputer | null>(null)
  const audioAnalyzerRef = useRef<AudioAnalyzer | null>(null)
  const mediaRecorderRef = useRef<MediaRecorder | null>(null)
  const audioChunksRef = useRef<Blob[]>([])
  const metricsBufferRef = useRef<Metrics[]>([])
  const postIntervalRef = useRef<ReturnType<typeof setInterval> | null>(null)
  const frameCountRef = useRef(0)
  const mediaStreamRef = useRef<MediaStream | null>(null)
  const lastPoseLandmarksRef = useRef<any[] | null>(null)
  const poseMissFramesRef = useRef(0)
  const liveAnalysisIntervalRef = useRef<ReturnType<typeof setInterval> | null>(null)
  const metricsWindowRef = useRef<Metrics[]>([])
  const behaviorStatsRef = useRef<BehaviorStats>(behaviorStats)
  const speechRecognitionRef = useRef<any>(null)
  const shouldRestartRecognitionRef = useRef(false)
  const isRecordingRef = useRef(false)
  const liveChunkIntervalRef = useRef<ReturnType<typeof setInterval> | null>(null)
  const liveChunkBusyRef = useRef(false)
  const liveChunkBufferRef = useRef<Blob[]>([])
  const liveChunkCumulativeRef = useRef<Blob[]>([])
  const warningSeenAtRef = useRef<Record<string, number>>({})

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
    return () => {
      closeMediaCapture()
      if (liveAnalysisIntervalRef.current) {
        clearInterval(liveAnalysisIntervalRef.current)
        liveAnalysisIntervalRef.current = null
      }
      if (liveChunkIntervalRef.current) {
        clearInterval(liveChunkIntervalRef.current)
        liveChunkIntervalRef.current = null
      }
      shouldRestartRecognitionRef.current = false
      if (speechRecognitionRef.current) {
        try {
          speechRecognitionRef.current.stop()
        } catch {
          // no-op
        }
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

  const startRecording = async () => {
    try {
      if (!isMediaPipeReady()) {
        alert('MediaPipe not ready')
        return
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

          mediaRecorderRef.current.ondataavailable = (e) => {
            if (e.data.size > 0) {
              audioChunksRef.current.push(e.data)
              liveChunkBufferRef.current.push(e.data)
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

      // Start posting metrics every 1s
      metricsBufferRef.current = []
      postIntervalRef.current = setInterval(async () => {
        if (metricsBufferRef.current.length > 0 && sessionId) {
          const events = metricsBufferRef.current.map(m => ({
            timestampMs: m.timestamp,
            type: 'combined',
            value: {
              eyeContact: m.eyeContact,
              headStability: m.headStability,
              posture: m.posture,
              fidget: m.fidget
            }
          }))

          try {
            await ApiService.postMetrics(sessionId, events)
          } catch (error) {
            console.error('Failed to post metrics:', error)
          }

          metricsBufferRef.current = []
        }
      }, 1000)

      setLiveTranscriptLines([])
      setLiveTranscriptInterim('')
      liveChunkBufferRef.current = []
      liveChunkCumulativeRef.current = []
      warningSeenAtRef.current = {}
      setWarningHistory([])
      setIsRecording(true)
      isRecordingRef.current = true
      startLiveTranscript()
      if (liveChunkIntervalRef.current) {
        clearInterval(liveChunkIntervalRef.current)
      }
      liveChunkIntervalRef.current = setInterval(() => {
        void transcribeLiveChunks()
      }, 7000)
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
    stopLiveTranscript()
    if (liveChunkIntervalRef.current) {
      clearInterval(liveChunkIntervalRef.current)
      liveChunkIntervalRef.current = null
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

    closeMediaCapture()
    setFaceLandmarks(undefined)
    setPoseLandmarks(undefined)
    lastPoseLandmarksRef.current = null
    poseMissFramesRef.current = 0

    if (postIntervalRef.current) {
      clearInterval(postIntervalRef.current)
      postIntervalRef.current = null
    }
    if (liveAnalysisIntervalRef.current) {
      clearInterval(liveAnalysisIntervalRef.current)
      liveAnalysisIntervalRef.current = null
    }

    setIsRecording(false)
    isRecordingRef.current = false
    await recorderStopPromise
    await transcribeLiveChunks()
  }

  const startLiveTranscript = () => {
    const SpeechRecognitionCtor = (window as any).SpeechRecognition || (window as any).webkitSpeechRecognition
    if (!SpeechRecognitionCtor) {
      setLiveTranscriptSupported(false)
      return
    }

    setLiveTranscriptSupported(true)
    shouldRestartRecognitionRef.current = true

    if (!speechRecognitionRef.current) {
      const recognition = new SpeechRecognitionCtor()
      recognition.lang = session?.language === 'en' ? 'en-US' : 'tr-TR'
      recognition.continuous = true
      recognition.interimResults = true
      recognition.maxAlternatives = 1

      recognition.onresult = (event: any) => {
        let interim = ''
        const finalChunks: string[] = []
        for (let i = event.resultIndex; i < event.results.length; i++) {
          const result = event.results[i]
          const text = (result?.[0]?.transcript || '').trim()
          if (!text) continue
          if (result.isFinal) finalChunks.push(text)
          else interim += `${text} `
        }
        if (finalChunks.length > 0) {
          setLiveTranscriptLines(prev => [...prev, ...finalChunks].slice(-250))
        }
        setLiveTranscriptInterim(interim.trim())
      }

      recognition.onend = () => {
        if (shouldRestartRecognitionRef.current && isRecordingRef.current) {
          try {
            recognition.start()
          } catch {
            // no-op
          }
        }
      }

      recognition.onerror = (event: any) => {
        const err = event?.error || 'unknown'
        console.warn('Live transcript error:', err)
        if (err === 'not-allowed' || err === 'service-not-allowed' || err === 'audio-capture') {
          setLiveTranscriptSupported(false)
          shouldRestartRecognitionRef.current = false
        }
      }

      speechRecognitionRef.current = recognition
    }

    try {
      speechRecognitionRef.current.start()
    } catch {
      // no-op
    }
  }

  const stopLiveTranscript = () => {
    shouldRestartRecognitionRef.current = false
    setLiveTranscriptInterim('')
    if (speechRecognitionRef.current) {
      try {
        speechRecognitionRef.current.stop()
      } catch {
        // no-op
      }
    }
  }

  const transcribeLiveChunks = async () => {
    if (liveChunkBusyRef.current || liveChunkBufferRef.current.length === 0) return

    liveChunkBusyRef.current = true
    try {
      const newChunks = [...liveChunkBufferRef.current]
      liveChunkBufferRef.current = []
      liveChunkCumulativeRef.current.push(...newChunks)
      if (liveChunkCumulativeRef.current.length === 0) return

      const audioBlob = new Blob(liveChunkCumulativeRef.current, { type: 'audio/webm' })
      const formData = new FormData()
      formData.append('file', audioBlob, 'live.webm')

      const transcriptResult = await ApiService.transcribeAudio(formData, session?.language || 'tr')
      const fullText = (transcriptResult?.full_text || '').trim()
      if (fullText.length === 0) return

      const sentenceLines = fullText
        .split(/(?<=[.!?])\s+|\n+/)
        .map((line: string) => line.trim())
        .filter(Boolean)

      setLiveTranscriptLines(sentenceLines.slice(-250))
      setLiveTranscriptInterim('')
    } catch (error) {
      console.warn('Live chunk transcription skipped:', error)
    } finally {
      liveChunkBusyRef.current = false
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

      if (faceLm.length > 0) {
        const metrics = metricsComputerRef.current.computeFrame(
          faceLm,
          stablePoseLm
        )

        setCurrentMetrics(metrics)
        setBehaviorStats(metricsComputerRef.current.getBehaviorStats())
        metricsBufferRef.current.push(metrics)
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
      const audioBlob = new Blob(chunks, { type: 'audio/webm' })
      const formData = new FormData()
      formData.append('file', audioBlob, 'answer.webm')

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

  return (
    <div className="page interview-page">
      <div className="container">
        <h1>Interview Session - {session.selectedRole}</h1>

        <div className="interview-layout">
          <div className="question-panel">
            <div className="question-header">
              <h2>Question {currentQuestionIndex + 1} of {questions.length}</h2>
            </div>
            {currentQuestion && (
              <div className="question-content">
                <p className="question-text">{currentQuestion.prompt}</p>
              </div>
            )}
          </div>

          <div className="recording-panel">
            <div className="recording-live-grid">
              <div>
                <div className="video-stage">
                  <div className="mechanical-clock" title={clockNow.toLocaleTimeString('tr-TR')}>
                    <span className="clock-center" />
                    <span className="clock-hand hour" style={{ transform: `translateX(-50%) rotate(${mechanicalClock.hrDeg}deg)` }} />
                    <span className="clock-hand minute" style={{ transform: `translateX(-50%) rotate(${mechanicalClock.minDeg}deg)` }} />
                  </div>
                  {isRecording && mediaReady && (
                    <VideoCanvas
                      width={480}
                      height={360}
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

                  {!isRecording && (
                    <div className="camera-placeholder">
                      <p>Click "Start Recording" to begin</p>
                    </div>
                  )}
                </div>
              </div>

              <aside className="live-transcript-panel">
                <div className="live-transcript-header">Canli Konusma Metni</div>
                <div className="live-transcript-body">
                  {!liveTranscriptSupported && (
                    <div className="live-transcript-empty">
                      Tarayici ses tanima destegi yok. Metin, kayit sirasinda sunucu tarafinda 6-8 saniyede bir guncellenir.
                    </div>
                  )}
                  {liveTranscriptLines.length === 0 && !liveTranscriptInterim && (
                    <div className="live-transcript-empty">
                      Kayit baslayinca konusmaniz burada anlik yazacak (yaklasik 6-8 saniye gecikmeli).
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
                </div>
              </aside>
            </div>

            <div className="controls">
              <button
                onClick={isRecording ? stopRecording : startRecording}
                className={`btn ${isRecording ? 'btn-danger' : 'btn-success'}`}
              >
                {isRecording ? 'Stop Recording' : 'Start Recording'}
              </button>
              <button
                type="button"
                onClick={() => setShowOverlay(v => !v)}
                className="btn btn-secondary"
              >
                {showOverlay ? 'Overlay: ON' : 'Overlay: OFF'}
              </button>
            </div>

            <div className="overlay-controls">
              <button
                type="button"
                onClick={() => setShowFaceOverlay(v => !v)}
                className="btn btn-secondary btn-sm"
                disabled={!showOverlay}
              >
                {showFaceOverlay ? 'Face Details: ON' : 'Face Details: OFF'}
              </button>
              <button
                type="button"
                onClick={() => setShowPoseOverlay(v => !v)}
                className="btn btn-secondary btn-sm"
                disabled={!showOverlay}
              >
                {showPoseOverlay ? 'Body Details: ON' : 'Body Details: OFF'}
              </button>
              <button
                type="button"
                onClick={() => setShowDiagnosticsOverlay(v => !v)}
                className="btn btn-secondary btn-sm"
                disabled={!showOverlay}
              >
                {showDiagnosticsOverlay ? 'Live Stats: ON' : 'Live Stats: OFF'}
              </button>
            </div>

            {showOverlay && (
              <p className="overlay-info">
                Overlay acik: yuz mesh + agiz/cene durumu, vucut iskeleti + postur acilari ve canli landmark sayisi gosteriliyor.
              </p>
            )}

            {llmInsight && (
              <div className="llm-insight">
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

            {isRecording && (
              <LiveHints
                hints={coaching}
                metrics={currentMetrics}
                behaviorStats={behaviorStats}
                warningHistory={warningHistory}
              />
            )}
          </div>
        </div>

        <div className="nav-buttons">
          <button onClick={handleNext} disabled={uploading} className="btn btn-primary">
            {uploading ? 'Processing...' : currentQuestionIndex < questions.length - 1 ? 'Next Question' : 'Finish'}
          </button>
          {backgroundTranscribes > 0 && (
            <p className="subtitle">Onceki cevaplar arka planda isleniyor...</p>
          )}
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

