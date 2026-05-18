import { useState, useEffect } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import ApiService, {
  LlmCoachingResponse,
  ScoringPreviewResponse,
  ScoringProfilesResponse
} from '../services/ApiService'
import { triggerBlobDownload } from '../utils/download'
import { TranscriptModal } from '../components/TranscriptModal'
import {
  LineChart, Line, XAxis, YAxis, Tooltip, ResponsiveContainer, Legend
} from 'recharts'
import '../styles/pages.css'

interface ScoreMetric {
  label: string
  value: number
  icon: string
  description: string
}

interface ScoreCompareField {
  label: string
  key: 'eyeContactScore' | 'speakingRateScore' | 'fillerScore' | 'postureScore' | 'overallScore'
  fallbackKey: 'eyeContact' | 'speakingRate' | 'fillerWords' | 'posture' | 'overall'
}

interface DerivedPoint {
  windowStartMs: number
  windowEndMs: number
  value: number
}

interface TranscriptLine {
  startMs: number
  endMs: number
  text: string
  questionOrder?: number
}

interface BehavioralSummary {
  avgArousal: number
  avgStress: number
  avgSmile: number
  duchennePct: number
  sampleCount: number
}

interface ReportQuestion {
  id: string
  order: number
  prompt: string
  audioUrl?: string | null
  screenAudioUrl?: string | null
  startMs?: number | null
  endMs?: number | null
  createdAt?: string
  transcript?: TranscriptLine[]
  metrics?: Record<string, DerivedPoint[]>
  behavioral?: BehavioralSummary
}

const SCORE_COMPARE_FIELDS: ScoreCompareField[] = [
  { label: 'Eye Contact', key: 'eyeContactScore', fallbackKey: 'eyeContact' },
  { label: 'Speaking Rate', key: 'speakingRateScore', fallbackKey: 'speakingRate' },
  { label: 'Filler Words', key: 'fillerScore', fallbackKey: 'fillerWords' },
  { label: 'Posture', key: 'postureScore', fallbackKey: 'posture' },
  { label: 'Overall', key: 'overallScore', fallbackKey: 'overall' }
]

const API_BASE_URL = import.meta.env.VITE_API_URL || 'http://localhost:8080/api'

function Report() {
  const { sessionId } = useParams<{ sessionId: string }>()
  const navigate = useNavigate()
  const [report, setReport] = useState<any>(null)
  const [session, setSession] = useState<any>(null)
  const [loading, setLoading] = useState(true)
  const [llmCoaching, setLlmCoaching] = useState<LlmCoachingResponse | null>(null)
  const [llmLoading, setLlmLoading] = useState(false)
  const [llmError, setLlmError] = useState<string | null>(null)
  const [llmPolling, setLlmPolling] = useState(false)
  const [exportJsonLoading, setExportJsonLoading] = useState(false)
  const [exportMdLoading, setExportMdLoading] = useState(false)
  const [exportMessage, setExportMessage] = useState<string | null>(null)
  const [exportError, setExportError] = useState<string | null>(null)

  const [isScoringPanelOpen, setIsScoringPanelOpen] = useState(false)
  const [scoringProfiles, setScoringProfiles] = useState<ScoringProfilesResponse | null>(null)
  const [profilesLoading, setProfilesLoading] = useState(false)
  const [profilesError, setProfilesError] = useState<string | null>(null)
  const [currentProfile, setCurrentProfile] = useState<string>('default')
  const [selectedProfile, setSelectedProfile] = useState<string>('')
  const [previewResult, setPreviewResult] = useState<ScoringPreviewResponse | null>(null)
  const [previewLoading, setPreviewLoading] = useState(false)
  const [applyingLoading, setApplyingLoading] = useState(false)
  const [recalculatingLoading, setRecalculatingLoading] = useState(false)
  const [scoringMessage, setScoringMessage] = useState<string | null>(null)
  const [scoringError, setScoringError] = useState<string | null>(null)
  const [isTranscriptOpen, setIsTranscriptOpen] = useState(false)
  const [isProcessing, setIsProcessing] = useState(false)
  const [retranscribeStatus, setRetranscribeStatus] = useState<'idle' | 'running' | 'done'>('idle')
  const [retranscribeProgress, setRetranscribeProgress] = useState<{ done: number; total: number } | null>(null)

  useEffect(() => {
    void loadReport()
  }, [sessionId])

  // Auto-poll for AI coaching until it's ready
  useEffect(() => {
    if (!sessionId || llmCoaching) return
    let cancelled = false
    let attempts = 0
    const MAX_ATTEMPTS = 60 // 5 min max

    const poll = async () => {
      if (cancelled || llmCoaching) return
      try {
        const result = await ApiService.getCachedLlmCoaching(sessionId)
        if (result) {
          if (!cancelled) { setLlmCoaching(result); setLlmPolling(false) }
          return
        }
      } catch { /* ignore */ }

      attempts++
      if (!cancelled && attempts < MAX_ATTEMPTS) {
        setTimeout(poll, 5000)
      } else if (!cancelled) {
        setLlmPolling(false)
      }
    }

    setLlmPolling(true)
    void poll()
    return () => { cancelled = true }
  }, [sessionId, llmCoaching])

  // Poll every 5 s while session is still processing
  useEffect(() => {
    if (!isProcessing || !sessionId) return
    const id = setInterval(async () => {
      try {
        const sess = await ApiService.getSession(sessionId)
        if ((sess?.status || '').toLowerCase() === 'completed') {
          setIsProcessing(false)
          void loadReport()
        }
      } catch { /* keep polling */ }
    }, 5000)
    return () => clearInterval(id)
  }, [isProcessing, sessionId])

  const loadScoringProfiles = async (preferredProfile?: string) => {
    if (!sessionId) {
      setProfilesLoading(false)
      return
    }

    setProfilesLoading(true)
    setProfilesError(null)

    try {
      const profiles = await ApiService.getScoringProfiles()
      setScoringProfiles(profiles)

      const profileNames = Object.keys(profiles.profiles)
      const preferred = preferredProfile?.trim()

      if (preferred && profileNames.includes(preferred)) {
        setSelectedProfile(preferred)
      } else if (profiles.defaultProfile && profileNames.includes(profiles.defaultProfile)) {
        setSelectedProfile(profiles.defaultProfile)
      } else {
        setSelectedProfile(profileNames[0] ?? '')
      }
    } catch {
      setScoringProfiles(null)
      setProfilesError('Scoring profiles are unavailable right now.')
    } finally {
      setProfilesLoading(false)
    }
  }

  const loadReport = async () => {
    if (!sessionId) {
      setLoading(false)
      return
    }

    setLoading(true)
    setIsProcessing(false)

    try {
      const reportData = await ApiService.getReport(sessionId)
      const sessionData = reportData?.session ?? null

      setReport(reportData)
      setSession(sessionData)

      const resolvedCurrentProfile = sessionData?.scoringProfile || 'default'
      setCurrentProfile(resolvedCurrentProfile)

      const cachedCoaching = ApiService.extractCoachingFromReport(reportData)
      if (cachedCoaching) {
        setLlmCoaching(cachedCoaching)
      }

      await loadScoringProfiles(sessionData?.scoringProfile)
    } catch (error: any) {
      console.error('Report load failed:', error)
      // Check if session exists but is still processing
      if (sessionId) {
        try {
          const sess = await ApiService.getSession(sessionId)
          if (sess && (sess.status || '').toLowerCase() !== 'completed') {
            setIsProcessing(true)
            setReport(null)
            return
          }
        } catch { /* fall through to null */ }
      }
      setReport(null)
    } finally {
      setLoading(false)
    }
  }

  // Second-pass: re-transcribe questions that have audio but no transcript, using accurate mode.
  const retranscribeMissingQuestions = async (reportData: any) => {
    if (!sessionId) return
    const qs: ReportQuestion[] = Array.isArray(reportData?.questions) ? reportData.questions : []
    const missing = qs.filter(q =>
      q.audioUrl && resolveAudioUrl(q.audioUrl) &&
      (!q.transcript || q.transcript.length === 0)
    )
    if (missing.length === 0) return

    setRetranscribeStatus('running')
    setRetranscribeProgress({ done: 0, total: missing.length })

    for (let i = 0; i < missing.length; i++) {
      const q = missing[i]
      const url = resolveAudioUrl(q.audioUrl)!
      try {
        const audioRes = await fetch(url)
        if (!audioRes.ok) continue
        const audioBlob = await audioRes.blob()
        const ext = url.endsWith('.mp4') ? 'mp4' : 'webm'
        const formData = new FormData()
        formData.append('file', audioBlob, `answer.${ext}`)
        const sessionLang = (reportData?.session?.language === 'en') ? 'en' : 'tr'
        const result = await ApiService.transcribeAudio(formData, sessionLang, 'accurate')
        const segs = (result.segments ?? []).filter((s: any) => s.text?.trim())
        if (segs.length > 0) {
          const batch = segs.map((s: any) => ({
            clientSegmentId: crypto.randomUUID(),
            startMs: Math.max(0, Math.round(s.start_ms ?? 0)),
            endMs: Math.max(0, Math.round(s.end_ms ?? 0)),
            text: s.text.trim(),
            confidence: s.confidence,
            questionOrder: q.order
          }))
          await ApiService.postTranscriptBatch(sessionId, batch).catch(() => {})
        }
      } catch { /* skip this question, try next */ }
      setRetranscribeProgress({ done: i + 1, total: missing.length })
    }

    setRetranscribeStatus('done')
    setRetranscribeProgress(null)
    void loadReport()
  }

  useEffect(() => {
    if (report && !loading) {
      void retranscribeMissingQuestions(report)
    }
  }, [report])

  const parseLlmError = (error: any): string => {
    const status = error?.response?.status
    if (status === 502) {
      return 'AI coaching service returned invalid output. Please try again.'
    }

    return 'AI coaching is unavailable right now.'
  }

  const parseExportError = (error: any): string => {
    const status = error?.response?.status
    if (status === 404) {
      return 'Report export is not available for this session.'
    }

    return 'Export failed. Please try again.'
  }

  const parseScoringError = (error: any): string => {
    const status = error?.response?.status
    if (status === 400) {
      const detail = error?.response?.data?.detail
      return typeof detail === 'string' && detail.length > 0
        ? detail
        : 'Invalid scoring profile selection.'
    }

    if (status === 404) {
      return 'Session was not found.'
    }

    return 'Scoring operation failed. Please try again.'
  }

  const formatMs = (ms: number): string => {
    const totalSeconds = Math.max(0, Math.floor(ms / 1000))
    const minutes = Math.floor(totalSeconds / 60)
    const seconds = totalSeconds % 60
    return `${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}`
  }

  const formatRange = (range?: [number, number]): string => {
    if (!range || range.length !== 2) {
      return ''
    }

    return `${formatMs(range[0])}\u2013${formatMs(range[1])}`
  }

  const getScoreColor = (score: number): string => {
    if (score >= 80) return '#4caf50'
    if (score >= 60) return '#ff9800'
    if (score >= 40) return '#ff6f00'
    return '#d32f2f'
  }

  const getScoreGrade = (score: number): string => {
    if (score >= 90) return 'A'
    if (score >= 80) return 'B'
    if (score >= 70) return 'C'
    if (score >= 60) return 'D'
    return 'F'
  }

  const getSeverityTag = (severity: number): string => {
    if (severity >= 5) return 'Critical'
    if (severity >= 4) return 'Needs Work'
    if (severity >= 3) return 'Fair'
    if (severity >= 2) return 'Good'
    return 'Excellent'
  }

  const getScoreValue = (
    source: Record<string, unknown> | null | undefined,
    key: ScoreCompareField['key'],
    fallbackKey: ScoreCompareField['fallbackKey']
  ): number | null => {
    if (!source) {
      return null
    }

    const primary = source[key]
    if (typeof primary === 'number') {
      return primary
    }

    const fallback = source[fallbackKey]
    if (typeof fallback === 'number') {
      return fallback
    }

    return null
  }

  const resolveAudioUrl = (audioUrl?: string | null): string | null => {
    const trimmed = audioUrl?.trim()
    if (!trimmed) {
      return null
    }

    if (/^(https?:|blob:|data:)/i.test(trimmed)) {
      return trimmed
    }

    if (trimmed.startsWith('/')) {
      return `${new URL(API_BASE_URL, window.location.origin).origin}${trimmed}`
    }

    return trimmed
  }

  const generateAiCoaching = async () => {
    if (!sessionId) return

    setLlmLoading(true)
    setLlmError(null)

    try {
      const coaching = await ApiService.generateLlmCoaching(sessionId)
      setLlmCoaching(coaching)
    } catch (error) {
      setLlmError(parseLlmError(error))
    } finally {
      setLlmLoading(false)
    }
  }

  const showDownloadedMessage = (text: string) => {
    setExportMessage(text)
    window.setTimeout(() => setExportMessage(null), 2000)
  }

  const exportJson = async () => {
    if (!sessionId || exportJsonLoading) return

    setExportJsonLoading(true)
    setExportError(null)

    try {
      const file = await ApiService.downloadReportExportJson(sessionId)
      triggerBlobDownload(file.blob, file.filename)
      showDownloadedMessage('Downloaded JSON')
    } catch (error) {
      setExportError(parseExportError(error))
    } finally {
      setExportJsonLoading(false)
    }
  }

  const exportMarkdown = async () => {
    if (!sessionId || exportMdLoading) return

    setExportMdLoading(true)
    setExportError(null)

    try {
      const file = await ApiService.downloadReportExportMarkdown(sessionId)
      triggerBlobDownload(file.blob, file.filename)
      showDownloadedMessage('Downloaded Markdown')
    } catch (error) {
      setExportError(parseExportError(error))
    } finally {
      setExportMdLoading(false)
    }
  }

  const onSelectProfile = (profileName: string) => {
    setSelectedProfile(profileName)
    setPreviewResult(null)
    setScoringMessage(null)
    setScoringError(null)
  }

  const previewScoringProfile = async () => {
    if (!sessionId || !selectedProfile) {
      return
    }

    setPreviewLoading(true)
    setScoringError(null)
    setScoringMessage(null)

    try {
      const preview = await ApiService.previewScoringProfile(sessionId, selectedProfile)
      setPreviewResult(preview)
    } catch (error) {
      setPreviewResult(null)
      setScoringError(parseScoringError(error))
    } finally {
      setPreviewLoading(false)
    }
  }

  const applyScoringProfile = async () => {
    if (!sessionId || !selectedProfile) {
      return
    }

    setApplyingLoading(true)
    setScoringError(null)
    setScoringMessage(null)

    try {
      const result = await ApiService.setScoringProfile(sessionId, selectedProfile)
      setCurrentProfile(result.scoringProfile || selectedProfile)
      setSession((prev: any) => ({ ...(prev ?? {}), scoringProfile: result.scoringProfile || selectedProfile }))
      setScoringMessage('Scoring profile updated.')
    } catch (error) {
      setScoringError(parseScoringError(error))
    } finally {
      setApplyingLoading(false)
    }
  }

  const applyAndRecalculate = async () => {
    if (!sessionId || !selectedProfile) {
      return
    }

    setRecalculatingLoading(true)
    setScoringError(null)
    setScoringMessage(null)

    try {
      const profileResult = await ApiService.setScoringProfile(sessionId, selectedProfile)
      await ApiService.finalizeSession(sessionId)
      await loadReport()
      setCurrentProfile(profileResult.scoringProfile || selectedProfile)
      setPreviewResult(null)
      setScoringMessage('Profile applied and report recalculated.')
    } catch (error) {
      setScoringError(parseScoringError(error))
    } finally {
      setRecalculatingLoading(false)
    }
  }

  if (loading) {
    return (
      <div className="page">
        <div className="container">
          <p className="loading-text">Loading report...</p>
        </div>
      </div>
    )
  }

  if (!sessionId) {
    return (
      <div className="page">
        <div className="container">
          <p className="error-text">Session ID is missing.</p>
        </div>
      </div>
    )
  }

  if (isProcessing) {
    return (
      <div className="page report-page">
        <div className="report-processing-page">
          <div className="report-processing-spinner" />
          <h2>Preparing Report</h2>
          <p>Interview data is being processed, please wait...</p>
          <p className="report-processing-hint">This page will update automatically.</p>
          <button className="btn btn-secondary" onClick={() => navigate('/reports')}>
            Back to My Reports
          </button>
        </div>
      </div>
    )
  }

  if (!report) {
    return (
      <div className="page report-page">
        <div className="report-processing-page">
          <p className="error-text">Report not found.</p>
          <button className="btn btn-secondary" onClick={() => navigate('/reports')}>
            Back to My Reports
          </button>
        </div>
      </div>
    )
  }

  const scoreCard = report.scoreCard || {}
  const feedbackItems =
    report.feedbackItems ||
    (Array.isArray(report.patterns)
      ? report.patterns.map((pattern: any) => ({
          category: pattern.type,
          severity: pattern.severity,
          title: pattern.type,
          details: pattern.evidence,
          suggestion: '',
          exampleText: ''
        }))
      : [])

  const questions: ReportQuestion[] = Array.isArray(report.questions)
    ? [...report.questions].sort((a: ReportQuestion, b: ReportQuestion) => (a.order ?? 0) - (b.order ?? 0))
    : []

  const metrics: ScoreMetric[] = [
    {
      label: 'Eye Contact',
      value: scoreCard.eyeContactScore ?? scoreCard.eyeContact ?? 0,
      icon: 'Eye',
      description: 'Maintain steady eye contact with the interviewer.'
    },
    {
      label: 'Speaking Rate',
      value: scoreCard.speakingRateScore ?? scoreCard.speakingRate ?? 0,
      icon: 'Voice',
      description: 'Ideal pace is around 120-160 words per minute.'
    },
    {
      label: 'Filler Words',
      value: scoreCard.fillerScore ?? scoreCard.fillerWords ?? 0,
      icon: 'Fluency',
      description: 'Reduce filler patterns and keep sentence flow clear.'
    },
    {
      label: 'Posture',
      value: scoreCard.postureScore ?? scoreCard.posture ?? 0,
      icon: 'Posture',
      description: 'Keep shoulders balanced and body language stable.'
    }
  ]

  const overall = scoreCard.overallScore ?? scoreCard.overall ?? 0

  const transcriptSegmentsRaw: Array<{ startMs: number; endMs: number; text: string; questionOrder?: number }> =
    Array.isArray(report.transcript) && report.transcript.length > 0
      ? report.transcript
      : Array.isArray(report.transcriptSegments)
        ? report.transcriptSegments
        : Array.isArray(report.session?.transcriptSegments)
          ? report.session.transcriptSegments
          : []

  // Sort by question order first, then by startMs within each question
  const transcriptSegments = [...transcriptSegmentsRaw].sort((a, b) => {
    const qA = a.questionOrder ?? 0
    const qB = b.questionOrder ?? 0
    if (qA !== qB) return qA - qB
    return a.startMs - b.startMs
  })

  // Group segments by question for display
  const transcriptByQuestion = transcriptSegments.reduce<Record<number, Array<{ startMs: number; text: string }>>>((acc, seg) => {
    const q = seg.questionOrder ?? 0
    if (!acc[q]) acc[q] = []
    acc[q].push({ startMs: seg.startMs, text: seg.text })
    return acc
  }, {})

  const transcriptText = transcriptSegments.length > 0
    ? transcriptSegments.map((s) => s.text).join('\n')
    : (report.transcript?.full_text ?? report.session?.transcript?.full_text ?? (typeof report.session?.transcript === 'string' ? report.session.transcript : null))
  const sessionDate = session?.createdAt ? new Date(session.createdAt).toLocaleDateString('en-US', { day: '2-digit', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit' }) : null
  const sessionRole = session?.selectedRole ?? session?.role ?? 'Not specified'
  const sessionLang = session?.language === 'en' ? 'English' : 'Turkish'
  const questionCount = questions.length

  return (
    <div className="page report-page" data-testid="report-page">
      <div className="report-shell">

        {/* ── PROFESSIONAL REPORT HEADER ── */}
        <div className="report-hero-header">
          <div className="report-hero-left">
            <span className="eyebrow">Interview Report</span>
            <h1 className="report-hero-title">Interview Performance Report</h1>
            <div className="report-hero-meta">
              <span className="report-meta-chip">🎯 {sessionRole}</span>
              <span className="report-meta-chip">🌐 {sessionLang}</span>
              <span className="report-meta-chip">❓ {questionCount} Questions</span>
              {sessionDate && <span className="report-meta-chip">📅 {sessionDate}</span>}
            </div>
          </div>
          <div className="report-hero-score">
            <div className="report-hero-score-ring" style={{ '--score-deg': `${(overall / 100) * 360}deg`, '--score-color': getScoreColor(overall) } as any}>
              <div className="report-hero-score-inner">
                <span className="report-hero-score-val">{overall}</span>
                <span className="report-hero-score-lbl">/ 100</span>
              </div>
            </div>
            <div className="report-hero-grade" style={{ color: getScoreColor(overall) }}>Grade {getScoreGrade(overall)}</div>
            <div className="report-hero-verdict">
              {overall >= 80 ? '🏆 Excellent Performance' : overall >= 60 ? '👍 Strong Baseline with Room to Grow' : '📈 Progress Possible with Targeted Practice'}
            </div>
          </div>
        </div>

        <div className="overall-score-section">
          <div className="overall-score-card">
            <div className="overall-score-value" style={{ color: getScoreColor(overall) }}>
              {overall}
            </div>
            <div className="overall-score-label">Overall Score</div>
            <div className="overall-score-grade">Grade: {getScoreGrade(overall)}</div>
            <div className="overall-score-message">
              {overall >= 80 && 'Excellent performance.'}
              {overall >= 60 && overall < 80 && 'Strong baseline with clear improvement areas.'}
              {overall < 60 && 'Progress possible with targeted practice.'}
            </div>
          </div>
        </div>

        <div className="metrics-section">
          <h2>Performance Breakdown</h2>
          <div className="metrics-grid">
            {metrics.map((metric, idx) => (
              <div key={idx} className="metric-card">
                <div className="metric-icon">{metric.icon}</div>
                <div className="metric-label">{metric.label}</div>
                <div className="metric-score-container">
                  <div
                    className="metric-score-circle"
                    style={{
                      background: `conic-gradient(${getScoreColor(metric.value)} 0deg ${(metric.value / 100) * 360}deg, #1b2748 ${(metric.value / 100) * 360}deg 360deg)`,
                      borderColor: getScoreColor(metric.value)
                    }}
                  >
                    <div className="metric-score-value">{metric.value}</div>
                  </div>
                </div>
                <div className="metric-description">{metric.description}</div>
              </div>
            ))}
          </div>
        </div>

        <div className="feedback-section question-audio-section" data-testid="question-audio-section">
          <h2>🎙️ Interview Recordings &amp; Transcript</h2>

          {retranscribeStatus === 'running' && (
            <div className="retranscribe-banner retranscribe-banner--running">
              Checking transcripts… re-transcribing missing questions with high-accuracy mode
              {retranscribeProgress && ` (${retranscribeProgress.done} / ${retranscribeProgress.total})`}
            </div>
          )}
          {retranscribeStatus === 'done' && (
            <div className="retranscribe-banner retranscribe-banner--done">
              High-accuracy transcription complete
            </div>
          )}

          {questions.length > 0 ? (
            <div className="question-audio-list">
              {questions.map((question) => {
                const webcamSrc = resolveAudioUrl(question.audioUrl)
                const screenSrc = resolveAudioUrl(question.screenAudioUrl)
                const qTranscript = Array.isArray(question.transcript) ? question.transcript : []
                const qMetrics = question.metrics ?? {}
                const behavioral = question.behavioral

                // Build chart data: merge all metric keys by time
                const chartData = (() => {
                  const keys = ['eyeContact', 'posture', 'fidget', 'headJitter'] as const
                  const timeMap = new Map<number, Record<string, number>>()
                  keys.forEach(key => {
                    ;(qMetrics[key] ?? []).forEach((pt: DerivedPoint) => {
                      if (!timeMap.has(pt.windowStartMs)) timeMap.set(pt.windowStartMs, { t: pt.windowStartMs })
                      timeMap.get(pt.windowStartMs)![key] = Math.round(pt.value * 100)
                    })
                  })
                  return Array.from(timeMap.values()).sort((a, b) => a.t - b.t)
                })()

                const hasMetrics = chartData.length > 0

                return (
                  <div key={question.id || question.order} className="interview-qa-card" data-testid={`question-audio-card-${question.order}`}>
                    <div className="interview-qa-header">
                      <span className="interview-qa-num">Question {question.order}</span>
                      {question.startMs != null && question.endMs != null && (
                        <span className="interview-qa-duration">
                          {formatMs(question.startMs)} – {formatMs(question.endMs)}
                        </span>
                      )}
                    </div>
                    <div className="interview-qa-prompt">{question.prompt}</div>

                    {/* ── Video + Metrics Row ── */}
                    <div className="interview-qa-body">
                      <div className={`interview-qa-videos${screenSrc ? ' interview-qa-videos--dual' : ''}`}>
                        {/* Webcam */}
                        <div className="interview-qa-video-block">
                          <div className="interview-qa-video-label">Webcam</div>
                          {webcamSrc ? (
                            <video controls preload="metadata" src={webcamSrc}
                              className="interview-qa-video-el"
                              data-testid={`question-audio-player-${question.order}`}>
                              Your browser does not support video playback.
                            </video>
                          ) : (
                            <div className="interview-qa-no-video">
                              <span>📹</span><p>No recording found</p>
                            </div>
                          )}
                        </div>

                        {/* Screen recording (optional) */}
                        {screenSrc && (
                          <div className="interview-qa-video-block">
                            <div className="interview-qa-video-label">Screen</div>
                            <video controls preload="metadata" src={screenSrc}
                              className="interview-qa-video-el">
                              Your browser does not support video playback.
                            </video>
                          </div>
                        )}

                        {/* Metrics chart */}
                        {hasMetrics && (
                          <div className="interview-qa-metrics">
                            <div className="interview-qa-video-label">Metrics</div>
                            <ResponsiveContainer width="100%" height={180}>
                              <LineChart data={chartData} margin={{ top: 4, right: 8, left: -24, bottom: 0 }}>
                                <XAxis dataKey="t" hide />
                                <YAxis domain={[0, 100]} tickCount={3} tick={{ fontSize: 10, fill: '#888' }} />
                                <Tooltip
                                  contentStyle={{ background: '#1a1b2e', border: '1px solid #333', borderRadius: 8, fontSize: 12 }}
                                  formatter={(v: any, name: any) => [`${v}%`, name]}
                                  labelFormatter={() => ''}
                                />
                                <Legend iconType="circle" iconSize={8} wrapperStyle={{ fontSize: 11 }} />
                                <Line type="monotone" dataKey="eyeContact" name="Eye Contact" stroke="#7c83ff" dot={false} strokeWidth={2} />
                                <Line type="monotone" dataKey="posture" name="Posture" stroke="#4caf50" dot={false} strokeWidth={2} />
                                <Line type="monotone" dataKey="fidget" name="Fidget" stroke="#ff9800" dot={false} strokeWidth={1.5} />
                              </LineChart>
                            </ResponsiveContainer>
                          </div>
                        )}
                      </div>

                      {/* ── Behavioral signals ── */}
                      {behavioral && behavioral.sampleCount > 0 && (
                        <div className="interview-qa-behavioral">
                          <div className="interview-qa-video-label">Behavioral Signals</div>
                          <div className="behavioral-bars">
                            <div className="behavioral-bar-row">
                              <span className="behavioral-bar-label">Arousal</span>
                              <div className="behavioral-bar-track">
                                <div className="behavioral-bar-fill behavioral-bar-fill--arousal" style={{ width: `${Math.min(100, behavioral.avgArousal)}%` }} />
                              </div>
                              <span className="behavioral-bar-value">{Math.round(behavioral.avgArousal)}%</span>
                            </div>
                            <div className="behavioral-bar-row">
                              <span className="behavioral-bar-label">Stress</span>
                              <div className="behavioral-bar-track">
                                <div className="behavioral-bar-fill behavioral-bar-fill--stress" style={{ width: `${Math.min(100, behavioral.avgStress)}%` }} />
                              </div>
                              <span className="behavioral-bar-value">{Math.round(behavioral.avgStress)}%</span>
                            </div>
                            <div className="behavioral-bar-row">
                              <span className="behavioral-bar-label">Smile</span>
                              <div className="behavioral-bar-track">
                                <div className="behavioral-bar-fill behavioral-bar-fill--smile" style={{ width: `${Math.min(100, behavioral.avgSmile)}%` }} />
                              </div>
                              <span className="behavioral-bar-value">{Math.round(behavioral.avgSmile)}%</span>
                            </div>
                            <div className="behavioral-bar-row">
                              <span className="behavioral-bar-label">Genuine Smile</span>
                              <div className="behavioral-bar-track">
                                <div className="behavioral-bar-fill behavioral-bar-fill--duchenne" style={{ width: `${Math.min(100, behavioral.duchennePct)}%` }} />
                              </div>
                              <span className="behavioral-bar-value">{Math.round(behavioral.duchennePct)}%</span>
                            </div>
                          </div>
                        </div>
                      )}

                      {/* ── Per-soru transkript ── */}
                      {qTranscript.length > 0 && (
                        <div className="interview-qa-transcript">
                          <div className="interview-qa-transcript-header">
                            <span>📄</span> Transcript
                          </div>
                          <div className="interview-qa-transcript-body">
                            {qTranscript.map((seg, i) => (
                              <div key={i} className="transcript-segment-row">
                                <span className="transcript-timestamp">{formatMs(seg.startMs)}</span>
                                <span className="transcript-text">{seg.text}</span>
                              </div>
                            ))}
                          </div>
                        </div>
                      )}
                    </div>
                  </div>
                )
              })}
            </div>
          ) : (
            <div className="no-feedback"><p>No question recordings found.</p></div>
          )}

          {(transcriptSegments.length > 0 || transcriptText) && (
            <div className="interview-full-transcript interview-full-transcript--expanded">
              <div className="interview-full-transcript-header">
                <span>📄</span>
                <h3>Interview Transcript</h3>
              </div>
              <div className="interview-full-transcript-body">
                {transcriptSegments.length > 0
                  ? Object.entries(transcriptByQuestion)
                      .sort(([a], [b]) => Number(a) - Number(b))
                      .map(([qOrder, segs]) => (
                        <div key={qOrder} className="transcript-question-block">
                          <div className="transcript-question-label">
                            Question {Number(qOrder) > 0 ? qOrder : '—'}
                          </div>
                          {segs.map((seg, i) => (
                            <div key={i} className="transcript-segment-row">
                              <span className="transcript-timestamp">{formatMs(seg.startMs)}</span>
                              <span className="transcript-text">{seg.text}</span>
                            </div>
                          ))}
                        </div>
                      ))
                  : transcriptText
                }
              </div>
            </div>
          )}

          <TranscriptModal
            isOpen={isTranscriptOpen}
            onClose={() => setIsTranscriptOpen(false)}
            transcript={transcriptText || ''}
            segments={transcriptSegments}
            questionCount={questionCount}
          />
        </div>

        <div className="ai-coach-section" data-testid="ai-coach-section">
          <div className="ai-coach-header">
            <h2>Scoring Profile</h2>
            <button
              type="button"
              className="btn btn-secondary"
              onClick={() => setIsScoringPanelOpen((prev) => !prev)}
            >
              {isScoringPanelOpen ? 'Collapse' : 'Expand'}
            </button>
          </div>

          {isScoringPanelOpen && (
            <div className="ai-coach-content">
              <p className="report-subtitle" style={{ marginBottom: 12 }}>
                Current profile: <strong>{currentProfile || 'default'}</strong>
              </p>

              {profilesLoading ? (
                <p className="loading-text" style={{ padding: '10px 0' }}>Loading scoring profiles...</p>
              ) : profilesError || !scoringProfiles ? (
                <p className="ai-coach-error">{profilesError || 'Scoring profiles are unavailable.'}</p>
              ) : (
                <>
                  <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap', alignItems: 'center', marginBottom: 12 }}>
                    <select
                      value={selectedProfile}
                      onChange={(e) => onSelectProfile(e.target.value)}
                      className="btn btn-secondary"
                      disabled={previewLoading || applyingLoading || recalculatingLoading}
                    >
                      {Object.keys(scoringProfiles.profiles).map((name) => (
                        <option key={name} value={name}>
                          {name}
                        </option>
                      ))}
                    </select>

                    <button
                      type="button"
                      className="btn btn-secondary"
                      onClick={previewScoringProfile}
                      disabled={previewLoading || applyingLoading || recalculatingLoading || !selectedProfile}
                    >
                      {previewLoading ? 'Previewing...' : 'Preview Score'}
                    </button>

                    <button
                      type="button"
                      className="btn btn-secondary"
                      onClick={applyScoringProfile}
                      disabled={applyingLoading || previewLoading || recalculatingLoading || !selectedProfile}
                    >
                      {applyingLoading ? 'Applying...' : 'Apply Profile'}
                    </button>

                    <button
                      type="button"
                      className="btn btn-primary"
                      onClick={applyAndRecalculate}
                      disabled={recalculatingLoading || applyingLoading || previewLoading || !selectedProfile}
                    >
                      {recalculatingLoading ? 'Recalculating...' : 'Apply + Recalculate'}
                    </button>
                  </div>

                  {scoringMessage && <p className="report-export-success">{scoringMessage}</p>}
                  {scoringError && <p className="report-export-error">{scoringError}</p>}

                  {previewResult && (
                    <div className="feedback-card" style={{ marginTop: 12 }}>
                      <div className="feedback-title">Score Preview Comparison</div>
                      <div className="feedback-details">Stored vs previewed values for profile "{previewResult.profileName}"</div>
                      <div style={{ display: 'grid', gap: 8 }}>
                        {SCORE_COMPARE_FIELDS.map((field) => {
                          const currentValue =
                            getScoreValue(previewResult.currentStoredScoreCard as any, field.key, field.fallbackKey) ??
                            getScoreValue(scoreCard as any, field.key, field.fallbackKey)
                          const previewValue = getScoreValue(previewResult.scoreCardPreview as any, field.key, field.fallbackKey)

                          const delta =
                            currentValue !== null && previewValue !== null
                              ? previewValue - currentValue
                              : null

                          const deltaLabel =
                            delta === null
                              ? 'n/a'
                              : delta === 0
                                ? '0'
                                : `${delta > 0 ? '+' : ''}${delta}`

                          const deltaColor =
                            delta === null || delta === 0
                              ? '#6b7280'
                              : delta > 0
                                ? '#2e7d32'
                                : '#c62828'

                          return (
                            <div
                              key={field.key}
                              style={{
                                display: 'grid',
                                gridTemplateColumns: '1fr auto auto auto',
                                gap: 12,
                                alignItems: 'center',
                                padding: '8px 12px',
                                border: '1px solid rgba(255,255,255,0.1)',
                                borderRadius: 8,
                                background: 'rgba(255,255,255,0.04)'
                              }}
                            >
                              <strong style={{ color: 'var(--lx-text-primary, #e5e2e1)' }}>{field.label}</strong>
                              <span style={{ color: 'rgba(229,226,225,0.6)', fontSize: 13 }}>Current: {currentValue ?? 'n/a'}</span>
                              <span style={{ color: 'rgba(229,226,225,0.6)', fontSize: 13 }}>Preview: {previewValue ?? 'n/a'}</span>
                              <span style={{ color: deltaColor, fontWeight: 700, fontSize: 13 }}>Delta: {deltaLabel}</span>
                            </div>
                          )
                        })}
                      </div>
                    </div>
                  )}
                </>
              )}
            </div>
          )}
        </div>

        <div className="feedback-section">
          <h2>Detailed Feedback</h2>
          {feedbackItems.length > 0 ? (
            <div className="feedback-list">
              {feedbackItems.map((item: any, idx: number) => (
                <div key={idx} className="feedback-card">
                  <div className="feedback-header">
                    <div className="feedback-severity-badge">
                      <span className="severity-level">{getSeverityTag(item.severity)}</span>
                    </div>
                    <div className="feedback-category">{item.category}</div>
                  </div>
                  <div className="feedback-title">{item.title}</div>
                  <div className="feedback-details">{item.details}</div>
                  {item.suggestion && (
                    <div className="feedback-suggestion">
                      <strong>Suggestion:</strong> {item.suggestion}
                    </div>
                  )}
                  {item.exampleText && (
                    <div className="feedback-example">
                      <strong>Example:</strong> {item.exampleText}
                    </div>
                  )}
                </div>
              ))}
            </div>
          ) : (
            <div className="no-feedback">
              <p>No feedback items. Great result.</p>
            </div>
          )}
        </div>

        <div className="ai-coach-section">
          <div className="ai-coach-header">
            <h2>AI Coach</h2>
            {!llmPolling && !llmCoaching && (
              <button
                type="button"
                className="btn btn-primary"
                data-testid="generate-ai-coaching-button"
                onClick={generateAiCoaching}
                disabled={llmLoading}
              >
                {llmLoading ? 'Generating...' : 'Generate AI Coaching'}
              </button>
            )}
          </div>

          {llmPolling && !llmCoaching && (
            <div className="ai-coach-generating">
              <span className="processing-dot" style={{ display: 'inline-block', width: 10, height: 10, borderRadius: '50%', background: '#6366f1', marginRight: 10 }} />
              AI coaching hazırlanıyor... (bu işlem 1-2 dakika sürebilir)
            </div>
          )}

          {llmError && <div className="ai-coach-error">{llmError}</div>}

          {llmCoaching ? (
            <div className="ai-coach-content">
              <div className="ai-rubric-grid">
                <div className="ai-rubric-card">
                  <div className="ai-rubric-label">Technical Correctness</div>
                  <div className="ai-rubric-value">{llmCoaching.rubric.technical_correctness}/5</div>
                </div>
                <div className="ai-rubric-card">
                  <div className="ai-rubric-label">Depth</div>
                  <div className="ai-rubric-value">{llmCoaching.rubric.depth}/5</div>
                </div>
                <div className="ai-rubric-card">
                  <div className="ai-rubric-label">Structure</div>
                  <div className="ai-rubric-value">{llmCoaching.rubric.structure}/5</div>
                </div>
                <div className="ai-rubric-card">
                  <div className="ai-rubric-label">Clarity</div>
                  <div className="ai-rubric-value">{llmCoaching.rubric.clarity}/5</div>
                </div>
                <div className="ai-rubric-card">
                  <div className="ai-rubric-label">Confidence</div>
                  <div className="ai-rubric-value">{llmCoaching.rubric.confidence}/5</div>
                </div>
                <div className="ai-rubric-card ai-rubric-overall">
                  <div className="ai-rubric-label">Overall</div>
                  <div className="ai-rubric-value">{llmCoaching.overall}/100</div>
                </div>
              </div>

              <div className="ai-feedback-list">
                <h3>Feedback</h3>
                {llmCoaching.feedback.map((item, idx) => (
                  <div key={idx} className="ai-feedback-card">
                    <div className="ai-feedback-top">
                      <span className={`ai-category-badge category-${item.category}`}>{item.category}</span>
                      <span className="ai-severity">Severity {item.severity}/5</span>
                    </div>
                    <div className="ai-feedback-title">{item.title}</div>
                    <div className="ai-feedback-evidence"><strong>Evidence:</strong> {item.evidence}</div>
                    <div className="ai-feedback-suggestion"><strong>Suggestion:</strong> {item.suggestion}</div>
                    <div className="ai-feedback-example"><strong>Example:</strong> {item.example_phrase}</div>
                    {item.time_range_ms && (
                      <div className="ai-feedback-time">{formatRange(item.time_range_ms)}</div>
                    )}
                  </div>
                ))}
              </div>

              <div className="ai-drills">
                <h3>Drills</h3>
                {llmCoaching.drills.map((drill, idx) => (
                  <div key={idx} className="ai-drill-card">
                    <div className="ai-drill-title">
                      {drill.title} ({drill.duration_min} min)
                    </div>
                    <ul>
                      {drill.steps.map((step, stepIdx) => (
                        <li key={stepIdx}>{step}</li>
                      ))}
                    </ul>
                  </div>
                ))}
              </div>
            </div>
          ) : (
            <div className="no-feedback">
              <p>AI coaching is not generated yet.</p>
            </div>
          )}
        </div>

        <div className="report-actions" data-testid="report-actions">
          <button onClick={() => navigate('/')} className="btn btn-primary">
            Start New Interview
          </button>
          <button onClick={() => navigate('/reports')} className="btn btn-secondary">
            All Reports
          </button>
          <button
            onClick={exportJson}
            className="btn btn-secondary"
            data-testid="export-json-button"
            disabled={exportJsonLoading}
          >
            {exportJsonLoading ? 'Exporting JSON...' : 'Export JSON'}
          </button>
          <button
            onClick={exportMarkdown}
            className="btn btn-secondary"
            data-testid="export-markdown-button"
            disabled={exportMdLoading}
          >
            {exportMdLoading ? 'Exporting Markdown...' : 'Export Markdown'}
          </button>
          <button onClick={() => window.print()} className="btn btn-secondary">
            Print Report
          </button>
        </div>
        {exportMessage && <p className="report-export-success" data-testid="report-export-success">{exportMessage}</p>}
        {exportError && <p className="report-export-error" data-testid="report-export-error">{exportError}</p>}
      </div>
    </div>
  )
}

export default Report
