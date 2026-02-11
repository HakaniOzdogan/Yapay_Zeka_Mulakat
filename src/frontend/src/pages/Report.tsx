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
    loadReport()
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
    if (score >= 80) return '#4caf50' // Green
    if (score >= 60) return '#ff9800' // Orange
    if (score >= 40) return '#ff6f00' // Dark Orange
    return '#d32f2f' // Red
  }

  const getScoreGrade = (score: number): string => {
    if (score >= 90) return 'A'
    if (score >= 80) return 'B'
    if (score >= 70) return 'C'
    if (score >= 60) return 'D'
    return 'F'
  }

  const getSeverityIcon = (severity: number): string => {
    if (severity >= 5) return '🔴'
    if (severity >= 4) return '🟠'
    if (severity >= 3) return '🟡'
    return '🟢'
  }

  if (loading) return (
    <div className="page">
      <div className="container">
        <p className="loading-text">Loading report...</p>
      </div>
    </div>
  )
  
  if (!report) return (
    <div className="page">
      <div className="container">
        <p className="error-text">Report not found</p>
      </div>
    </div>
  )

  const scoreCard = report.scoreCard || {}
  const feedbackItems = report.feedbackItems || []

  const metrics: ScoreMetric[] = [
    {
      label: 'Eye Contact',
      value: scoreCard.eyeContactScore || 0,
      icon: '👁️',
      description: 'Maintain steady eye contact with the interviewer'
    },
    {
      label: 'Speaking Rate',
      value: scoreCard.speakingRateScore || 0,
      icon: '🗣️',
      description: 'Ideal pace is 120-160 words per minute'
    },
    {
      label: 'Filler Words',
      value: scoreCard.fillerScore || 0,
      icon: '💬',
      description: 'Reduce \'um\', \'uh\', \'like\' fillers'
    },
    {
      label: 'Posture',
      value: scoreCard.postureScore || 0,
      icon: '🧍',
      description: 'Sit upright and minimize fidgeting'
    }
  ]

  return (
    <div className="page report-page">
      <div className="container">
        <div className="report-header">
          <h1>Interview Report</h1>
          <p className="report-subtitle">
            {session?.selectedRole ? `Role: ${session.selectedRole}` : 'Interview Analysis'}
          </p>
        </div>

        {/* Overall Score Section */}
        <div className="overall-score-section">
          <div className="overall-score-card">
            <div className="overall-score-value" style={{ color: getScoreColor(scoreCard.overallScore || 0) }}>
              {scoreCard.overallScore || 0}
            </div>
            <div className="overall-score-label">Overall Score</div>
            <div className="overall-score-grade">Grade: {getScoreGrade(scoreCard.overallScore || 0)}</div>
            <div className="overall-score-message">
              {(scoreCard.overallScore || 0) >= 80 && '🎉 Excellent performance!'}
              {(scoreCard.overallScore || 0) >= 60 && (scoreCard.overallScore || 0) < 80 && '👍 Good effort! Room for improvement.'}
              {(scoreCard.overallScore || 0) < 60 && '💪 Keep practicing! You\'ll improve.'}
            </div>
          </div>
        </div>

        {/* Metrics Grid */}
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
                      background: `conic-gradient(${getScoreColor(metric.value)} 0deg ${(metric.value / 100) * 360}deg, #f0f0f0 ${(metric.value / 100) * 360}deg 360deg)`,
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

        {/* Feedback Section */}
        <div className="feedback-section">
          <h2>Detailed Feedback</h2>
          {feedbackItems.length > 0 ? (
            <div className="feedback-list">
              {feedbackItems.map((item: any, idx: number) => (
                <div key={idx} className="feedback-card">
                  <div className="feedback-header">
                    <div className="feedback-severity-badge">
                      <span className="severity-icon">{getSeverityIcon(item.severity)}</span>
                      <span className="severity-level">
                        {item.severity === 1 ? 'Excellent' : 
                         item.severity === 2 ? 'Good' : 
                         item.severity === 3 ? 'Fair' : 
                         item.severity === 4 ? 'Needs Work' : 
                         'Critical'}
                      </span>
                    </div>
                    <div className="feedback-category">{item.category}</div>
                  </div>
                  <div className="feedback-title">{item.title}</div>
                  <div className="feedback-details">{item.details}</div>
                  {item.suggestion && (
                    <div className="feedback-suggestion">
                      <strong>💡 Suggestion:</strong> {item.suggestion}
                    </div>
                  )}
                  {item.exampleText && (
                    <div className="feedback-example">
                      <strong>📝 Example:</strong> {item.exampleText}
                    </div>
                  )}
                </div>
              ))}
            </div>
          ) : (
            <div className="no-feedback">
              <p>No feedback items. Great job!</p>
            </div>
          )}
        </div>

        {/* Action Buttons */}
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
