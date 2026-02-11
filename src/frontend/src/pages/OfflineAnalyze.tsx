import { useState, useEffect } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import ApiService from '../services/ApiService'
import '../styles/pages.css'

function OfflineAnalyze() {
  const location = useLocation()
  const navigate = useNavigate()
  const sessionId = (location.state as any)?.sessionId
  const [uploadedFile, setUploadedFile] = useState<File | null>(null)
  const [analyzing, setAnalyzing] = useState(false)
  const [loading, setLoading] = useState(false)
  const [session, setSession] = useState<any>(null)
  const [error, setError] = useState('')
  const [progress, setProgress] = useState(0)

  useEffect(() => {
    if (sessionId) {
      loadSession()
    }
  }, [sessionId])

  const loadSession = async () => {
    try {
      setLoading(true)
      const sess = await ApiService.getSession(sessionId)
      setSession(sess)

      // Seed questions if not already seeded
      let qs = await ApiService.getQuestions(sessionId)
      if (!qs || qs.length === 0) {
        await ApiService.seedQuestions(sessionId)
      }
    } catch (error) {
      console.error('Failed to load session:', error)
      setError('Failed to load session. Please go back and try again.')
    } finally {
      setLoading(false)
    }
  }

  const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    if (!file) return

    setError('')
    // Validate file size (max 100MB)
    if (file.size > 100 * 1024 * 1024) {
      setError('File size too large. Maximum 100MB allowed.')
      return
    }

    // Validate file type
    const validTypes = ['audio/webm', 'audio/mp4', 'audio/mpeg', 'audio/wav', 'audio/ogg', 'video/mp4', 'video/webm']
    if (!validTypes.includes(file.type)) {
      setError('Invalid file type. Please upload audio or video file.')
      return
    }

    setUploadedFile(file)
  }

  const formatFileSize = (bytes: number) => {
    if (bytes === 0) return '0 Bytes'
    const k = 1024
    const sizes = ['Bytes', 'KB', 'MB']
    const i = Math.floor(Math.log(bytes) / Math.log(k))
    return Math.round(bytes / Math.pow(k, i) * 100) / 100 + ' ' + sizes[i]
  }

  const handleAnalyze = async () => {
    if (!uploadedFile) {
      setError('Please select a file')
      return
    }

    if (!sessionId) {
      setError('Session not found. Please go back and create a new session.')
      return
    }

    setAnalyzing(true)
    setError('')
    setProgress(0)

    try {
      // Upload to speech-service
      setProgress(25)
      const formData = new FormData()
      formData.append('file', uploadedFile)

      const speechResponse = await ApiService.transcribeAudio(formData, session?.language || 'tr')
      if (!speechResponse) {
        throw new Error('No response from transcription service')
      }

      setProgress(50)

      // Store transcript in backend
      if (speechResponse.segments && sessionId) {
        await ApiService.storeTranscript(sessionId, {
          segments: speechResponse.segments,
          stats: {
            duration_ms: speechResponse.duration_ms,
            word_count: speechResponse.word_count,
            wpm: speechResponse.wpm,
            filler_count: speechResponse.filler_count,
            pause_count: speechResponse.pause_count
          }
        })
      }

      setProgress(75)

      // Finalize session and generate report
      await ApiService.finalizeSession(sessionId)

      setProgress(100)

      // Navigate to report
      setTimeout(() => {
        navigate(`/report/${sessionId}`)
      }, 500)
    } catch (error) {
      console.error('Analysis failed:', error)
      setError(error instanceof Error ? error.message : 'Analysis failed. Please try again.')
    } finally {
      setAnalyzing(false)
    }
  }

  return (
    <div className="page offline-page">
      <div className="container">
        <h1>Offline Analysis</h1>
        <p className="subtitle">Upload your interview recording for detailed analysis</p>

        {loading && <div className="loading-spinner">Loading session...</div>}

        {!loading && session && (
          <>
            <div className="session-info">
              <p><strong>Role:</strong> {session.selectedRole}</p>
              <p><strong>Language:</strong> {session.language === 'tr' ? 'Türkçe' : 'English'}</p>
            </div>

            <div className="upload-area">
              <div className="upload-box">
                <input
                  type="file"
                  accept="audio/*,video/*"
                  onChange={handleFileChange}
                  id="file-input"
                  disabled={analyzing}
                  style={{ display: 'none' }}
                />
                <label htmlFor="file-input" className="upload-label">
                  <div className="upload-icon">📁</div>
                  <p>Click to select audio or video file</p>
                  <p className="upload-hint">(Max 100MB)</p>
                  {uploadedFile && (
                    <div className="selected-file-info">
                      <p className="selected-file-name">{uploadedFile.name}</p>
                      <p className="selected-file-size">{formatFileSize(uploadedFile.size)}</p>
                    </div>
                  )}
                </label>
              </div>

              {error && (
                <div className="error-message">
                  <p>⚠️ {error}</p>
                </div>
              )}

              <button
                onClick={handleAnalyze}
                disabled={!uploadedFile || analyzing || loading}
                className="btn btn-primary"
              >
                {analyzing ? (
                  <>
                    <span className="spinner"></span>
                    Analyzing... ({progress}%)
                  </>
                ) : (
                  'Analyze Interview'
                )}
              </button>

              {analyzing && (
                <div className="progress-bar">
                  <div className="progress-fill" style={{ width: `${progress}%` }}></div>
                </div>
              )}
            </div>
          </>
        )}

        {!loading && error && !session && (
          <div className="error-container">
            <p>{error}</p>
            <button onClick={() => navigate('/')} className="btn btn-secondary">
              ← Go Back to Home
            </button>
          </div>
        )}
      </div>
    </div>
  )
}

export default OfflineAnalyze
