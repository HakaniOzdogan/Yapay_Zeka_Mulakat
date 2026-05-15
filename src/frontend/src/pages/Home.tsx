import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import ApiService from '../services/ApiService'
import '../styles/pages.css'

function Home() {
  const navigate = useNavigate()
  const [selectedRole, setSelectedRole] = useState('Software Engineer')
  const [selectedLanguage, setSelectedLanguage] = useState('tr')
  const [selectedDifficulty, setSelectedDifficulty] = useState<'easy' | 'medium' | 'hard'>('medium')
  const [selectedMode, setSelectedMode] = useState<'realtime' | 'offline'>('realtime')
  const [loading, setLoading] = useState(false)

  const roles = [
    'Software Engineer',
    'Product Manager',
    'Data Scientist',
    'UX Designer'
  ]

  const languages = [
    { code: 'tr', label: 'Turkish' },
    { code: 'en', label: 'English' }
  ]

  const difficulties = [
    { value: 'easy', label: 'Easy' },
    { value: 'medium', label: 'Medium' },
    { value: 'hard', label: 'Hard' }
  ] as const

  const handleStart = async () => {
    setLoading(true)
    try {
      const session = await ApiService.createSession(selectedRole, selectedLanguage, selectedDifficulty)
      const createdSessionId = session?.id || session?.sessionId

      if (!createdSessionId) {
        throw new Error('Session ID was missing in createSession response.')
      }

      if (selectedMode === 'realtime') {
        navigate(`/interview/${createdSessionId}`)
      } else {
        navigate('/offline', { state: { sessionId: createdSessionId } })
      }
    } catch (error) {
      console.error('Failed to create session:', error)
      alert('Could not connect. Please check that the backend service is running.')
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="page home-page hero-home">
      <div className="home-shell">
        <section className="home-hero-grid">
          <div>
            <span className="eyebrow">AI Interview Studio</span>
            <h1 className="hero-title">
              The future of <em>interview</em> experience is here.
            </h1>
            <p className="hero-copy">
              Improve your technical, behavioral, and communication performance with AI-powered simulations in one flow.
              Live feedback, transcript, and detailed analysis on the same platform.
            </p>
            <div className="hero-actions">
              <button
                onClick={handleStart}
                disabled={loading}
                className={`btn btn-primary ${loading ? 'animate-pulse-glow' : ''}`}
              >
                {loading ? 'Setting up session...' : 'Get Started'}
              </button>
              <button type="button" className="btn btn-secondary" onClick={() => navigate('/reports')}>
                View Past Reports
              </button>
            </div>

            <div className="hero-stats">
              <div className="stat-card">
                <span className="stat-value">Real-Time</span>
                <span className="stat-label">Live coaching and vision metrics</span>
              </div>
              <div className="stat-card">
                <span className="stat-value">Transcript</span>
                <span className="stat-label">Real-time and session-end speech transcription</span>
              </div>
              <div className="stat-card">
                <span className="stat-value">AI Report</span>
                <span className="stat-label">Competency-based strengths and development areas</span>
              </div>
            </div>
          </div>

          <div className="hero-visual">
            <div className="hero-visual-frame">
              <span className="pulse-badge">AI is analyzing</span>
              <div className="pulse-orb">
                <div className="pulse-icon">||</div>
              </div>
            </div>
            <div className="hero-floating-card">
              <div className="eyebrow" style={{ marginBottom: 10 }}>Live Coach</div>
              <p style={{ margin: 0 }}>
                Monitor eye contact, pace, posture, and content quality in one panel.
              </p>
            </div>
          </div>
        </section>

        <section className="home-section">
          <span className="eyebrow">How it works</span>
          <div className="feature-grid">
            <article className="feature-card primary">
              <div className="feature-icon">1</div>
              <h3>Role-specific questions</h3>
              <p>Session is auto-generated based on selected role and language, question flow is tailored to the position.</p>
            </article>
            <article className="feature-card secondary">
              <div className="feature-icon">2</div>
              <h3>Live feedback</h3>
              <p>Speech rate, eye contact, posture, and hints panel stay with you throughout the interview.</p>
            </article>
            <article className="feature-card tertiary">
              <div className="feature-icon">3</div>
              <h3>Deep analysis report</h3>
              <p>At session end, your development path becomes clear with scores, feedback, and AI coaching.</p>
            </article>
          </div>
        </section>

        <section className="config-section">
          <div className="config-panel glass-card">
            <span className="eyebrow">Session Setup</span>
            <h2>Start your interview now.</h2>
            <p>
              Make your selection and we'll create a session and take you to the live interview screen.
            </p>
          </div>

          <div className="form config-panel">
            <div className="form-group">
              <label>Select Position</label>
              <select value={selectedRole} onChange={(e) => setSelectedRole(e.target.value)}>
                {roles.map((role) => (
                  <option key={role} value={role}>{role}</option>
                ))}
              </select>
            </div>

            <div className="config-grid">
              <div className="form-group">
                <label>Select Language</label>
                <select value={selectedLanguage} onChange={(e) => setSelectedLanguage(e.target.value)}>
                  {languages.map((lang) => (
                    <option key={lang.code} value={lang.code}>{lang.label}</option>
                  ))}
                </select>
              </div>

              <div className="form-group">
                <label>Difficulty Level</label>
                <select value={selectedDifficulty} onChange={(e) => setSelectedDifficulty(e.target.value as typeof selectedDifficulty)}>
                  {difficulties.map((d) => (
                    <option key={d.value} value={d.value}>{d.label}</option>
                  ))}
                </select>
              </div>
            </div>

            <div className="form-group">
              <label>Select Mode</label>
              <div className="mode-options">
                <label>
                  <input
                    type="radio"
                    name="mode"
                    value="realtime"
                    checked={selectedMode === 'realtime'}
                    onChange={(e) => setSelectedMode(e.target.value as 'realtime' | 'offline')}
                  />
                  Real-time interview session
                </label>
                <label>
                  <input
                    type="radio"
                    name="mode"
                    value="offline"
                    checked={selectedMode === 'offline'}
                    onChange={(e) => setSelectedMode(e.target.value as 'realtime' | 'offline')}
                  />
                  Upload and analyze later
                </label>
              </div>
            </div>

            <button
              onClick={handleStart}
              disabled={loading}
              className={`btn btn-primary ${loading ? 'animate-pulse-glow' : ''}`}
            >
              {loading ? 'Setting up session...' : 'Start Interview'}
            </button>
          </div>
        </section>
      </div>
    </div>
  )
}

export default Home
