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

  const roles = [
    'Software Engineer', 
    'Product Manager', 
    'Data Scientist', 
    'UX Designer'
  ]
  
  const languages = [
    { code: 'tr', label: 'Türkçe' },
    { code: 'en', label: 'English' }
  ]

  const handleStart = async () => {
    setLoading(true)
    try {
      const session = await ApiService.createSession(selectedRole, selectedLanguage)
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
      alert('Failed to connect to the Lumina Interview System. Please verify the environment.')
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
              Gelecegin <em>mulakat</em> deneyimi burada.
            </h1>
            <p className="hero-copy">
              Yapay zeka destekli simulasyonlarla teknik, davranissal ve iletisim performansinizi tek bir akista gelistirin.
              Canli geri bildirim, transcript ve detayli analiz ayni platformda.
            </p>
            <div className="hero-actions">
              <button
                onClick={handleStart}
                disabled={loading}
                className={`btn btn-primary ${loading ? 'animate-pulse-glow' : ''}`}
              >
                {loading ? 'Oturum hazirlaniyor...' : 'Hemen Basla'}
              </button>
              <button type="button" className="btn btn-secondary" onClick={() => navigate('/reports')}>
                Ornek Analizleri Gor
              </button>
            </div>

            <div className="hero-stats">
              <div className="stat-card">
                <span className="stat-value">Realtime</span>
                <span className="stat-label">Canli koçluk ve vizyon metrikleri</span>
              </div>
              <div className="stat-card">
                <span className="stat-value">Transcript</span>
                <span className="stat-label">Anlik ve oturum sonu konusma dokumu</span>
              </div>
              <div className="stat-card">
                <span className="stat-value">AI Report</span>
                <span className="stat-label">Yetkinlik bazli guclu ve gelisim alanlari</span>
              </div>
            </div>
          </div>

          <div className="hero-visual">
            <div className="hero-visual-frame">
              <span className="pulse-badge">AI analiz ediyor</span>
              <div className="pulse-orb">
                <div className="pulse-icon">||</div>
              </div>
            </div>
            <div className="hero-floating-card">
              <div className="eyebrow" style={{ marginBottom: 10 }}>Canli Koç</div>
              <p style={{ margin: 0 }}>
                Goz temasi, tempo, durus ve icerik kalitesini tek panelde izleyin.
              </p>
            </div>
          </div>
        </section>

        <section className="home-section">
          <span className="eyebrow">Surec nasil isler</span>
          <div className="feature-grid">
            <article className="feature-card primary">
              <div className="feature-icon">1</div>
              <h3>Role uygun sorular</h3>
              <p>Secilen role ve dile gore seans otomatik uretilir, soru akisi pozisyona gore sekillenir.</p>
            </article>
            <article className="feature-card secondary">
              <div className="feature-icon">2</div>
              <h3>Canli geribildirim</h3>
              <p>Konusma hizi, goz iletisimi, postur ve ipucu paneli mulakat boyunca sizinle kalir.</p>
            </article>
            <article className="feature-card tertiary">
              <div className="feature-icon">3</div>
              <h3>Derin analiz raporu</h3>
              <p>Oturum sonunda skor, feedback ve AI coaching ile gelisim yolunuzu net gorursunuz.</p>
            </article>
          </div>
        </section>

        <section className="config-section">
          <div className="config-panel glass-card">
            <span className="eyebrow">Oturum kurulum</span>
            <h2>Mulakatinizi simdi baslatin.</h2>
            <p>
              Ayni tasarim diliyle hazirlanan bu panel artik dogrudan proje akisinin parcasi. Seciminizi yapin, API oturumu olustursun
              ve sizi canli mulakat ekranina tasiyalim.
            </p>
          </div>

          <div className="form config-panel">
            <div className="form-group">
              <label>Select Role</label>
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

              <div className="config-card">
                <h3>Hazir durum</h3>
                <p style={{ marginBottom: 0 }}>Kamera, mikrofon ve analiz servisleri ayni akista kullanilir.</p>
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
                  Offline upload and later analysis
                </label>
              </div>
            </div>

            <button
              onClick={handleStart}
              disabled={loading}
              className={`btn btn-primary ${loading ? 'animate-pulse-glow' : ''}`}
            >
              {loading ? 'Oturum hazirlaniyor...' : 'Mulakati Baslat'}
            </button>
          </div>
        </section>
      </div>
    </div>
  )
}

export default Home
