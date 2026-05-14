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
    { code: 'tr', label: 'Türkçe' },
    { code: 'en', label: 'English' }
  ]

  const difficulties = [
    { value: 'easy', label: 'Kolay' },
    { value: 'medium', label: 'Orta' },
    { value: 'hard', label: 'Zor' }
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
      alert('Bağlantı kurulamadı. Lütfen backend servisinin çalıştığını kontrol edin.')
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="page home-page hero-home">
      <div className="home-shell">
        <section className="home-hero-grid">
          <div>
            <span className="eyebrow">Yapay Zeka Mülakat Stüdyosu</span>
            <h1 className="hero-title">
              Geleceğin <em>mülakat</em> deneyimi burada.
            </h1>
            <p className="hero-copy">
              Yapay zeka destekli simülasyonlarla teknik, davranışsal ve iletişim performansınızı tek bir akışta geliştirin.
              Canlı geri bildirim, transkript ve detaylı analiz aynı platformda.
            </p>
            <div className="hero-actions">
              <button
                onClick={handleStart}
                disabled={loading}
                className={`btn btn-primary ${loading ? 'animate-pulse-glow' : ''}`}
              >
                {loading ? 'Oturum hazırlanıyor...' : 'Hemen Başla'}
              </button>
              <button type="button" className="btn btn-secondary" onClick={() => navigate('/reports')}>
                Geçmiş Analizleri Gör
              </button>
            </div>

            <div className="hero-stats">
              <div className="stat-card">
                <span className="stat-value">Gerçek Zamanlı</span>
                <span className="stat-label">Canlı koçluk ve vizyon metrikleri</span>
              </div>
              <div className="stat-card">
                <span className="stat-value">Transkript</span>
                <span className="stat-label">Anlık ve oturum sonu konuşma dökümü</span>
              </div>
              <div className="stat-card">
                <span className="stat-value">AI Rapor</span>
                <span className="stat-label">Yetkinlik bazlı güçlü ve gelişim alanları</span>
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
              <div className="eyebrow" style={{ marginBottom: 10 }}>Canlı Koç</div>
              <p style={{ margin: 0 }}>
                Göz teması, tempo, duruş ve içerik kalitesini tek panelde izleyin.
              </p>
            </div>
          </div>
        </section>

        <section className="home-section">
          <span className="eyebrow">Süreç nasıl işler</span>
          <div className="feature-grid">
            <article className="feature-card primary">
              <div className="feature-icon">1</div>
              <h3>Role uygun sorular</h3>
              <p>Seçilen role ve dile göre seans otomatik üretilir, soru akışı pozisyona göre şekillenir.</p>
            </article>
            <article className="feature-card secondary">
              <div className="feature-icon">2</div>
              <h3>Canlı geri bildirim</h3>
              <p>Konuşma hızı, göz iletişimi, postür ve ipucu paneli mülakat boyunca sizinle kalır.</p>
            </article>
            <article className="feature-card tertiary">
              <div className="feature-icon">3</div>
              <h3>Derin analiz raporu</h3>
              <p>Oturum sonunda skor, geri bildirim ve AI coaching ile gelişim yolunuzu net görürsünüz.</p>
            </article>
          </div>
        </section>

        <section className="config-section">
          <div className="config-panel glass-card">
            <span className="eyebrow">Oturum kurulumu</span>
            <h2>Mülakatınızı şimdi başlatın.</h2>
            <p>
              Seçiminizi yapın, API oturumu oluştursun ve sizi canlı mülakat ekranına taşıyalım.
            </p>
          </div>

          <div className="form config-panel">
            <div className="form-group">
              <label>Pozisyon Seçin</label>
              <select value={selectedRole} onChange={(e) => setSelectedRole(e.target.value)}>
                {roles.map((role) => (
                  <option key={role} value={role}>{role}</option>
                ))}
              </select>
            </div>

            <div className="config-grid">
              <div className="form-group">
                <label>Dil Seçin</label>
                <select value={selectedLanguage} onChange={(e) => setSelectedLanguage(e.target.value)}>
                  {languages.map((lang) => (
                    <option key={lang.code} value={lang.code}>{lang.label}</option>
                  ))}
                </select>
              </div>

              <div className="form-group">
                <label>Zorluk Seviyesi</label>
                <select value={selectedDifficulty} onChange={(e) => setSelectedDifficulty(e.target.value as typeof selectedDifficulty)}>
                  {difficulties.map((d) => (
                    <option key={d.value} value={d.value}>{d.label}</option>
                  ))}
                </select>
              </div>
            </div>

            <div className="form-group">
              <label>Mod Seçin</label>
              <div className="mode-options">
                <label>
                  <input
                    type="radio"
                    name="mode"
                    value="realtime"
                    checked={selectedMode === 'realtime'}
                    onChange={(e) => setSelectedMode(e.target.value as 'realtime' | 'offline')}
                  />
                  Gerçek zamanlı mülakat oturumu
                </label>
                <label>
                  <input
                    type="radio"
                    name="mode"
                    value="offline"
                    checked={selectedMode === 'offline'}
                    onChange={(e) => setSelectedMode(e.target.value as 'realtime' | 'offline')}
                  />
                  Sonradan yükle ve analiz et
                </label>
              </div>
            </div>

            <button
              onClick={handleStart}
              disabled={loading}
              className={`btn btn-primary ${loading ? 'animate-pulse-glow' : ''}`}
            >
              {loading ? 'Oturum hazırlanıyor...' : 'Mülakatı Başlat'}
            </button>
          </div>
        </section>
      </div>
    </div>
  )
}

export default Home
