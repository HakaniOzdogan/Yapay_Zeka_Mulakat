import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import ApiService from '../services/ApiService'
import '../styles/pages.css'

interface SessionItem {
  id: string
  createdAt: string
  status: string
  selectedRole: string
  language: string
  overallScore?: number | null
}

function ReportsList() {
  const navigate = useNavigate()
  const [loading, setLoading] = useState(true)
  const [sessions, setSessions] = useState<SessionItem[]>([])

  useEffect(() => {
    void load()
  }, [])

  const load = async () => {
    try {
      const data = await ApiService.getRecentSessions(50)
      setSessions(Array.isArray(data) ? data : [])
    } catch (error) {
      console.error('Failed to load reports list:', error)
      setSessions([])
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="page reports-list-page">
      <div className="container">
        <h1>Gecmis Raporlar</h1>
        <p className="subtitle">Daha once yaptiginiz mulakatlar ve skor ozetleri</p>

        {loading && <p className="loading-text">Loading reports...</p>}

        {!loading && sessions.length === 0 && (
          <div className="reports-empty">
            Henuz kayitli bir mulakat yok.
          </div>
        )}

        {!loading && sessions.length > 0 && (
          <div className="reports-grid">
            {sessions.map((s) => (
              <article key={s.id} className="report-item-card">
                <div className="report-item-top">
                  <h3>{s.selectedRole}</h3>
                  <span className={`report-status status-${(s.status || '').toLowerCase()}`}>
                    {s.status || 'Unknown'}
                  </span>
                </div>
                <p className="report-item-meta">
                  {new Date(s.createdAt).toLocaleString('tr-TR')} • {s.language?.toUpperCase()}
                </p>
                <div className="report-item-score">
                  Score: {s.overallScore ?? '-'}
                </div>
                <div className="report-item-actions">
                  <button
                    type="button"
                    className="btn btn-primary"
                    onClick={() => navigate(`/report/${s.id}`)}
                  >
                    Raporu Ac
                  </button>
                  <button
                    type="button"
                    className="btn btn-secondary"
                    onClick={() => navigate(`/interview/${s.id}`)}
                  >
                    Oturuma Git
                  </button>
                </div>
              </article>
            ))}
          </div>
        )}
      </div>
    </div>
  )
}

export default ReportsList
