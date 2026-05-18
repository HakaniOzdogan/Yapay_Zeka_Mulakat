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

type SessionDisplayState = 'ready' | 'processing' | 'incomplete'

function getSessionState(status?: string, createdAt?: string): SessionDisplayState {
  if ((status || '').toLowerCase() === 'completed') return 'ready'
  const ageMs = Date.now() - new Date(createdAt || 0).getTime()
  return ageMs < 10 * 60 * 1000 ? 'processing' : 'incomplete'
}

function ReportsList() {
  const navigate = useNavigate()
  const [loading, setLoading] = useState(true)
  const [sessions, setSessions] = useState<SessionItem[]>([])
  const [deleteTarget, setDeleteTarget] = useState<SessionItem | null>(null)
  const [deleteBusy, setDeleteBusy] = useState(false)
  const [deleteModalError, setDeleteModalError] = useState('')
  const [deleteAllBusy, setDeleteAllBusy] = useState(false)
  const [message, setMessage] = useState('')
  const [errorMessage, setErrorMessage] = useState('')

  useEffect(() => {
    void load()
  }, [])

  // Poll every 5 s only while genuinely-processing sessions exist (< 10 min old, not completed)
  useEffect(() => {
    const hasProcessing = sessions.some(s => getSessionState(s.status, s.createdAt) === 'processing')
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

  const onDeleteAll = async () => {
    const confirmed = window.confirm(
      'Delete ALL your sessions? This removes every session in your account and cannot be undone.'
    )
    if (!confirmed) return

    setDeleteAllBusy(true)
    setErrorMessage('')

    try {
      const result = await ApiService.deleteAllSessions()
      setMessage(`Deleted ${result.deleted} session(s).`)
    } catch (error) {
      console.error('Delete all failed:', error)
      setErrorMessage('Failed to delete sessions. Please try again.')
    } finally {
      setDeleteAllBusy(false)
    }

    await load()
    window.setTimeout(() => setMessage(''), 3000)
  }

  const onAskDelete = (session: SessionItem) => {
    setDeleteTarget(session)
    setDeleteModalError('')
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
    setDeleteModalError('')

    let deleted = false
    try {
      await ApiService.deleteSession(deleteTarget.id)
      deleted = true
    } catch (error) {
      console.error('Failed to delete session:', error)
      setDeleteModalError('Silme başarısız oldu. Lütfen tekrar deneyin.')
    } finally {
      setDeleteBusy(false)
    }

    if (deleted) {
      setDeleteTarget(null)
      setMessage('Silindi')
      try {
        await load()
      } catch {
        // session was deleted; ignore reload error
      }
      window.setTimeout(() => setMessage(''), 2500)
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

        {!loading && sessions.length > 0 && (
          <div style={{ display: 'flex', justifyContent: 'flex-end', marginBottom: 12 }}>
            <button
              type="button"
              className="btn btn-secondary btn-danger"
              onClick={onDeleteAll}
              disabled={deleteAllBusy}
            >
              {deleteAllBusy ? 'Deleting...' : `Delete All (${sessions.length})`}
            </button>
          </div>
        )}

        {loading && <p className="loading-text">Loading reports...</p>}

        {!loading && sessions.length === 0 && (
          <div className="reports-empty" data-testid="sessions-empty-state">
            No interview sessions recorded yet.
          </div>
        )}

        {!loading && sessions.length > 0 && (
          <div className="reports-grid">
            {sessions.map((s) => {
              const state = getSessionState(s.status, s.createdAt)
              return (
                <article key={s.id} className={`report-item-card${state === 'ready' ? '' : ' report-item-card--processing'}`} data-testid={`session-card-${s.id}`}>
                  <div className="report-item-top">
                    <h3>{s.selectedRole || s.role || '-'}</h3>
                    {state === 'ready' && (
                      <span className="report-status status-completed">Ready</span>
                    )}
                    {state === 'processing' && (
                      <span className="report-status status-processing">
                        <span className="processing-dot" />
                        Processing...
                      </span>
                    )}
                    {state === 'incomplete' && (
                      <span className="report-status status-incomplete">Incomplete</span>
                    )}
                  </div>
                  <p className="report-item-meta">
                    {new Date(s.createdAt).toLocaleString('en-US')} | {(s.language || '-').toUpperCase()}
                  </p>
                  <div className="report-item-score">
                    {state === 'ready' ? `Score: ${s.overallScore ?? '-'}` : state === 'processing' ? 'Report is being prepared, please wait...' : 'Session incomplete — report may be partial.'}
                  </div>
                  <div className="report-item-actions">
                    <button
                      type="button"
                      className="btn btn-primary"
                      onClick={() => navigate(`/report/${s.id}`)}
                    >
                      Open Report
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
            <h3>Oturumu sil?</h3>
            <p>Bu işlem oturumu ve tüm verilerini kalıcı olarak siler.</p>
            {deleteModalError && (
              <p style={{ color: '#f44336', fontSize: '0.9rem', margin: '8px 0 0' }}>{deleteModalError}</p>
            )}
            <div className="confirm-modal-actions">
              <button type="button" className="btn btn-secondary" data-testid="delete-cancel-button" onClick={onCancelDelete} disabled={deleteBusy}>
                İptal
              </button>
              <button type="button" className="btn btn-danger" data-testid="delete-confirm-button" onClick={onConfirmDelete} disabled={deleteBusy}>
                {deleteBusy ? 'Siliniyor...' : 'Sil'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

export default ReportsList
