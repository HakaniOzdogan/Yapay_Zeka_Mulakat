import { useState, useEffect, useRef } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import ApiService from '../services/ApiService'
import { initMediaPipe, detectLandmarks, isMediaPipeReady } from '../services/MediaPipeService'
import { MetricsComputer, Metrics, BehaviorStats } from '../services/MetricsComputer'
import { generateCoachingHints, CoachingHint } from '../services/CoachingHints'
import { AudioAnalyzer } from '../services/AudioAnalyzer'
import { VideoCanvas } from '../components/VideoCanvas'
import { LiveHints } from '../components/LiveHints'
import { TranscriptModal } from '../components/TranscriptModal'
import '../styles/pages.css'

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
  const [videoStream, setVideoStream] = useState<MediaStream | null>(null)
  const [showOverlay, setShowOverlay] = useState(true)
  const [showFaceOverlay, setShowFaceOverlay] = useState(true)
  const [showPoseOverlay, setShowPoseOverlay] = useState(true)
  const [showDiagnosticsOverlay, setShowDiagnosticsOverlay] = useState(true)
  const [faceLandmarks, setFaceLandmarks] = useState<any[] | undefined>(undefined)
  const [poseLandmarks, setPoseLandmarks] = useState<any[] | undefined>(undefined)

  const metricsComputerRef = useRef<MetricsComputer | null>(null)
  const audioAnalyzerRef = useRef<AudioAnalyzer | null>(null)
  const mediaRecorderRef = useRef<MediaRecorder | null>(null)
  const audioChunksRef = useRef<Blob[]>([])
  const metricsBufferRef = useRef<Metrics[]>([])
  const postIntervalRef = useRef<NodeJS.Timeout | null>(null)
  const frameCountRef = useRef(0)
  const mediaStreamRef = useRef<MediaStream | null>(null)
  const lastPoseLandmarksRef = useRef<any[] | null>(null)
  const poseMissFramesRef = useRef(0)

  useEffect(() => {
    loadSession()
    initializeMediaPipe()
  }, [sessionId])

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

      setIsRecording(true)
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

      if (mediaStreamRef.current) {
        mediaStreamRef.current.getTracks().forEach(track => track.stop())
        mediaStreamRef.current = null
      }
      setVideoStream(null)
      setIsRecording(false)
    }
  }

  const stopRecording = async () => {
    if (!mediaStreamRef.current) return

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

    mediaStreamRef.current.getTracks().forEach(track => track.stop())
    mediaStreamRef.current = null
    setVideoStream(null)
    setFaceLandmarks(undefined)
    setPoseLandmarks(undefined)
    lastPoseLandmarksRef.current = null
    poseMissFramesRef.current = 0

    if (postIntervalRef.current) {
      clearInterval(postIntervalRef.current)
      postIntervalRef.current = null
    }

    setIsRecording(false)
    await recorderStopPromise
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

        // Update coaching hints
        const hints = generateCoachingHints(metrics)
        setCoaching(hints)
      }
    } catch (error) {
      console.error('Inference error:', error)
    }
  }

  const uploadAndTranscribe = async (): Promise<boolean> => {
    if (!sessionId || audioChunksRef.current.length === 0) {
      return false
    }

    setUploading(true)
    try {
      // Create FormData with audio blob
      const audioBlob = new Blob(audioChunksRef.current, { type: 'audio/webm' })
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

      setShowTranscript(true)
      audioChunksRef.current = []
      return true
    } catch (error) {
      console.error('Transcription failed:', error)
      return false
    } finally {
      setUploading(false)
    }
  }

  const handleNext = async () => {
    // Stop recording and transcribe before moving to next question
    if (isRecording) {
      await stopRecording()
    }

    if (audioChunksRef.current.length > 0) {
      try {
        const hasTranscript = await uploadAndTranscribe()
        if (hasTranscript) {
          return
        }
      } catch {
        // Continue to the next question even if transcription pipeline throws unexpectedly.
      }
    }

    // No audio recorded OR transcription failed: continue flow without blocking.
    proceedToNext()
  }

  const proceedToNext = () => {
    if (currentQuestionIndex < questions.length - 1) {
      setCurrentQuestionIndex(currentQuestionIndex + 1)
      setShowTranscript(false)
      setTranscriptData(null)
      audioChunksRef.current = []
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
                <p>📹 Click "Start Recording" to begin</p>
              </div>
            )}

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

            {isRecording && (
              <LiveHints hints={coaching} metrics={currentMetrics} behaviorStats={behaviorStats} />
            )}
          </div>
        </div>

        <div className="nav-buttons">
          <button onClick={handleNext} disabled={uploading} className="btn btn-primary">
            {uploading ? 'Processing...' : currentQuestionIndex < questions.length - 1 ? 'Next Question' : 'Finish'}
          </button>
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

