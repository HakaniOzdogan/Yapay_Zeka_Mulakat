import { useState, useEffect } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import ApiService, {
  LlmCoachingResponse,
  ScoringPreviewResponse,
  ScoringProfilesResponse
} from '../services/ApiService'
import { triggerBlobDownload } from '../utils/download'
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

interface ReportQuestion {
  id: string
  order: number
  prompt: string
  audioUrl?: string | null
  createdAt?: string
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

  useEffect(() => {
    void loadReport()
  }, [sessionId])

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

    try {
      const reportData = await ApiService.getReport(sessionId)
      const sessionData = reportData?.session ?? null

      setReport(reportData)
      setSession(sessionData)

      const resolvedCurrentProfile = sessionData?.scoringProfile || 'default'
      setCurrentProfile(resolvedCurrentProfile)

      await loadScoringProfiles(sessionData?.scoringProfile)

      const coaching = await ApiService.getLlmCoaching(sessionId)
      setLlmCoaching(coaching)
      setLlmError(null)
    } catch (error) {
      setLlmError(parseLlmError(error))
      setReport(null)
    } finally {
      setLoading(false)
    }
  }

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

  if (!report) {
    return (
      <div className="page">
        <div className="container">
          <p className="error-text">Report not found</p>
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

  return (
    <div className="page report-page" data-testid="report-page">
      <div className="report-shell">
        <div className="report-header">
          <div>
            <span className="eyebrow">Mulakat Analizi</span>
            <h1>Interview Report</h1>
            <p className="report-subtitle">
              {session?.selectedRole || session?.role
                ? `Role: ${session?.selectedRole ?? session?.role}`
                : 'Interview Analysis'}
            </p>
          </div>
          <div className="summary-card glass-card" style={{ padding: 24, borderRadius: 28, minWidth: 260 }}>
            <h3 style={{ marginBottom: 8 }}>Session</h3>
            <p style={{ marginBottom: 8 }}>{sessionId}</p>
            <p style={{ margin: 0 }}>Skor, feedback ve AI coaching tek raporda toplandi.</p>
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
          <h2>Sorular ve Ses Kayitlari</h2>
          {questions.length > 0 ? (
            <div className="question-audio-list">
              {questions.map((question) => {
                const audioSrc = resolveAudioUrl(question.audioUrl)

                return (
                  <div key={question.id || question.order} className="question-audio-card" data-testid={`question-audio-card-${question.order}`}>
                    <div className="question-audio-meta">Soru {question.order}</div>
                    <div className="question-audio-prompt">{question.prompt}</div>
                    {audioSrc ? (
                      <audio
                        className="question-audio-player"
                        controls
                        preload="metadata"
                        src={audioSrc}
                        data-testid={`question-audio-player-${question.order}`}
                      >
                        Audio playback is not supported by this browser.
                      </audio>
                    ) : (
                      <div className="question-audio-empty" data-testid={`question-audio-empty-${question.order}`}>
                        Ses kaydi yok.
                      </div>
                    )}
                  </div>
                )
              })}
            </div>
          ) : (
            <div className="no-feedback">
              <p>Soru kaydi bulunamadi.</p>
            </div>
          )}
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
                                padding: '8px 10px',
                                border: '1px solid #dbe4f0',
                                borderRadius: 8,
                                background: '#f9fbff'
                              }}
                            >
                              <strong>{field.label}</strong>
                              <span>Current: {currentValue ?? 'n/a'}</span>
                              <span>Preview: {previewValue ?? 'n/a'}</span>
                              <span style={{ color: deltaColor, fontWeight: 700 }}>Delta: {deltaLabel}</span>
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
            <button
              type="button"
              className="btn btn-primary"
              data-testid="generate-ai-coaching-button"
              onClick={generateAiCoaching}
              disabled={llmLoading}
            >
              {llmLoading ? 'Generating...' : 'Generate AI Coaching'}
            </button>
          </div>

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
