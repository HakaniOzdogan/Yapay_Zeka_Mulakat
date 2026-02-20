import { useState, useEffect } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import ApiService from '../services/ApiService'
import '../styles/pages.css'

interface ScoreMetric {
  label: string
  value: number
  icon: string
  description: string
}

function Report() {
  const { sessionId } = useParams<{ sessionId: string }>()
  const navigate = useNavigate()
  const [report, setReport] = useState<any>(null)
  const [session, setSession] = useState<any>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    void loadReport()
  }, [sessionId])

  const loadReport = async () => {
    if (!sessionId) return
    try {
      const [reportData, sessionData] = await Promise.all([
        ApiService.getReport(sessionId),
        ApiService.getSession(sessionId)
      ])
      setReport(reportData)
      setSession(sessionData)
    } catch (error) {
      console.error('Failed to load report:', error)
    } finally {
      setLoading(false)
    }
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

  if (loading) {
    return (
      <div className="page">
        <div className="container">
          <p className="loading-text">Loading report...</p>
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
  const feedbackItems = report.feedbackItems || []

  const metrics: ScoreMetric[] = [
    {
      label: 'Eye Contact',
      value: scoreCard.eyeContactScore || 0,
      icon: 'Eye',
      description: 'Maintain steady eye contact with the interviewer.'
    },
    {
      label: 'Speaking Rate',
      value: scoreCard.speakingRateScore || 0,
      icon: 'Voice',
      description: 'Ideal pace is around 120-160 words per minute.'
    },
    {
      label: 'Filler Words',
      value: scoreCard.fillerScore || 0,
      icon: 'Fluency',
      description: 'Reduce filler patterns and keep sentence flow clear.'
    },
    {
      label: 'Posture',
      value: scoreCard.postureScore || 0,
      icon: 'Posture',
      description: 'Keep shoulders balanced and body language stable.'
    }
  ]

  const overall = scoreCard.overallScore || 0

  return (
    <div className="page report-page">
      <div className="container">
        <div className="report-header">
          <h1>Interview Report</h1>
          <p className="report-subtitle">
            {session?.selectedRole ? `Role: ${session.selectedRole}` : 'Interview Analysis'}
          </p>
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

        <div className="report-actions">
          <button onClick={() => navigate('/')} className="btn btn-primary">
            Start New Interview
          </button>
          <button onClick={() => window.print()} className="btn btn-secondary">
            Print Report
          </button>
        </div>
      </div>
    </div>
  )
}

export default Report
