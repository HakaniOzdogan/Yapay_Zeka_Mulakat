import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import ApiService from '../services/ApiService'
import '../styles/pages.css'

function Home() {
  const navigate = useNavigate()
  const [selectedRole, setSelectedRole] = useState('Software Engineer')
  const [selectedLanguage, setSelectedLanguage] = useState('tr')
  const [selectedMode, setSelectedMode] = useState<'realtime' | 'offline'>('realtime')
  const [loading, setLoading] = useState(false)

  const roles = ['Software Engineer', 'Product Manager', 'Data Scientist', 'UX Designer']
  const languages = [
    { code: 'tr', label: 'Türkçe' },
    { code: 'en', label: 'English' }
  ]

  const handleStart = async () => {
    setLoading(true)
    try {
      const session = await ApiService.createSession(selectedRole, selectedLanguage)
      if (selectedMode === 'realtime') {
        navigate(`/interview/${session.id}`)
      } else {
        navigate('/offline', { state: { sessionId: session.id } })
      }
    } catch (error) {
      console.error('Failed to create session:', error)
      alert('Failed to create session. Please try again.')
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="page home-page">
      <div className="container">
        <h1>Interview Coach</h1>
        <p className="subtitle">Practice interviews with real-time AI coaching</p>

        <div className="form">
          <div className="form-group">
            <label>Select Role</label>
            <select value={selectedRole} onChange={(e) => setSelectedRole(e.target.value)}>
              {roles.map(role => (
                <option key={role} value={role}>{role}</option>
              ))}
            </select>
          </div>

          <div className="form-group">
            <label>Select Language</label>
            <select value={selectedLanguage} onChange={(e) => setSelectedLanguage(e.target.value)}>
              {languages.map(lang => (
                <option key={lang.code} value={lang.code}>{lang.label}</option>
              ))}
            </select>
          </div>

          <div className="form-group">
            <label>Select Mode</label>
            <div className="mode-options">
              <label>
                <input
                  type="radio"
                  value="realtime"
                  checked={selectedMode === 'realtime'}
                  onChange={(e) => setSelectedMode(e.target.value as 'realtime' | 'offline')}
                />
                Real-time (Live Coaching)
              </label>
              <label>
                <input
                  type="radio"
                  value="offline"
                  checked={selectedMode === 'offline'}
                  onChange={(e) => setSelectedMode(e.target.value as 'realtime' | 'offline')}
                />
                Offline (Upload & Analyze)
              </label>
            </div>
          </div>

          <button onClick={handleStart} disabled={loading} className="btn btn-primary">
            {loading ? 'Starting...' : 'Start Interview'}
          </button>
        </div>
      </div>
    </div>
  )
}

export default Home
