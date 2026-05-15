import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import ApiService from '../services/ApiService'
import '../styles/pages.css'

interface SessionItem {
  id: string
  createdAt: string
  status?: string
  selectedRole?: string
  role?: string
  language?: string
  overallScore?: number | null
}

function isSessionReady(status?: string): boolean {
  return (status || '').toLowerCase() === 'completed'
}

function ReportsList() {
  const navigate = useNavigate()
  const [loading, setLoading] = useState(true)
  const [sessions, setSessions] = useState<SessionItem[]>([])
  const [deleteTarget, setDeleteTarget] = useState<SessionItem | null>(null)
  const [deleteBusy, setDeleteBusy] = useState(false)
  const [message, setMessage] = useState('')
  const [errorMessage, setErrorMessage] = useState('')

  useEffect(() => {
    void load()
  }, [])

  // Poll every 5 s while any session is still processing
  useEffect(() => {
    const hasProcessing = sessions.some(s => !isSessionReady(s.status))
    if (!hasProcessing) return
    const id = setInterval(() => { void load() }, 5000)
    return () => clearInterval(id)
  }, [sessions])

  const load = async () => {
    try {
      const data = await ApiService.getRecentSessions(50)
      setSessions(Array.isArray(data) ? data : [])
      setErrorMessage('')
    } catch (error) {
      console.error('Failed to load reports list:', error)
      setErrorMessage('Failed to load sessions.')
      setSessions([])
    } finally {
      setLoading(false)
    }
  }

  const onAskDelete = (session: SessionItem) => {
    setDeleteTarget(session)
    setMessage('')
    setErrorMessage('')
  }

  const onCancelDelete = () => {
    if (deleteBusy) {
      return
    }

    setDeleteTarget(null)
  }

  const onConfirmDelete = async () => {
    if (!deleteTarget || deleteBusy) {
      return
    }

    setDeleteBusy(true)
    setErrorMessage('')

    try {
      await ApiService.deleteSession(deleteTarget.id)
      setDeleteTarget(null)
      setMessage('Deleted')
      await load()
      window.setTimeout(() => setMessage(''), 2500)
    } catch (error) {
      console.error('Failed to delete session:', error)
      setErrorMessage('Delete failed. Please try again.')
    } finally {
      setDeleteBusy(false)
    }
  }

  return (
    <div className="page reports-list-page" data-testid="sessions-page">
      <div className="reports-shell">
        <div className="reports-hero">
          <div>
            <span className="eyebrow">Candidate Panel</span>
            <h1>Past reports and analyses</h1>
            <p className="subtitle">Manage your interview sessions, score summaries, and re-openable sessions on this screen.</p>
          </div>
          <div className="summary-card glass-card" style={{ padding: 24, borderRadius: 28, minWidth: 260 }}>
            <h3 style={{ marginBottom: 8 }}>Total Sessions</h3>
            <div className="overall-score-value" style={{ fontSize: '3rem' }}>{sessions.length}</div>
            <p style={{ margin: 0 }}>All recorded sessions are listed here.</p>
          </div>
        </div>

        {message && <p className="reports-inline-success" data-testid="sessions-success-message">{message}</p>}
        {errorMessage && <p className="reports-inline-error" data-testid="sessions-error-message">{errorMessage}</p>}

        {loading && <p className="loading-text">Loading reports...</p>}

        {!loading && sessions.length === 0 && (
          <div className="reports-empty" data-testid="sessions-empty-state">
            No interview sessions recorded yet.
          </div>
        )}

        {!loading && sessions.length > 0 && (
          <div className="reports-grid">
            {sessions.map((s) => {
              const ready = isSessionReady(s.status)
              return (
                <article key={s.id} className={`report-item-card${ready ? '' : ' report-item-card--processing'}`} data-testid={`session-card-${s.id}`}>
                  <div className="report-item-top">
                    <h3>{s.selectedRole || s.role || '-'}</h3>
                    {ready ? (
                      <span className="report-status status-completed">Ready</span>
                    ) : (
                      <span className="report-status status-processing">
                        <span className="processing-dot" />
                        Processing...
                      </span>
                    )}
                  </div>
                  <p className="report-item-meta">
                    {new Date(s.createdAt).toLocaleString('en-US')} | {(s.language || '-').toUpperCase()}
                  </p>
                  <div className="report-item-score">
                    {ready ? `Score: ${s.overallScore ?? '-'}` : 'Report is being prepared, please wait...'}
                  </div>
                  <div className="report-item-actions">
                    <button
                      type="button"
                      className="btn btn-primary"
                      onClick={() => navigate(`/report/${s.id}`)}
                      disabled={!ready}
                      title={ready ? undefined : 'Report not ready yet'}
                    >
                      {ready ? 'Open Report' : 'Processing...'}
                    </button>
                    <button
                      type="button"
                      className="btn btn-secondary"
                      onClick={() => navigate(`/interview/${s.id}`)}
                    >
                      Go to Session
                    </button>
                    <button
                      type="button"
                      className="btn btn-secondary btn-danger"
                      data-testid={`session-delete-button-${s.id}`}
                      onClick={() => onAskDelete(s)}
                    >
                      Delete
                    </button>
                  </div>
                </article>
              )
            })}
          </div>
        )}
      </div>

      {deleteTarget && (
        <div className="confirm-modal-backdrop" role="dialog" aria-modal="true" data-testid="delete-confirm-modal">
          <div className="confirm-modal-card">
            <h3>Delete session?</h3>
            <p>This will permanently remove the session and its data.</p>
            <div className="confirm-modal-actions">
              <button type="button" className="btn btn-secondary" data-testid="delete-cancel-button" onClick={onCancelDelete} disabled={deleteBusy}>
                Cancel
              </button>
              <button type="button" className="btn btn-primary btn-danger-primary" data-testid="delete-confirm-button" onClick={onConfirmDelete} disabled={deleteBusy}>
                {deleteBusy ? 'Deleting...' : 'Delete'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

export default ReportsList
