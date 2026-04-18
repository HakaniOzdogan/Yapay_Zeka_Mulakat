import { FormEvent, useState } from 'react'
import { Navigate, useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'
import '../styles/pages.css'

type AuthMode = 'login' | 'register'

function AuthPage() {
  const navigate = useNavigate()
  const location = useLocation()
  const { isAuthenticated, loading, login, register } = useAuth()

  const [mode, setMode] = useState<AuthMode>('login')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  if (!loading && isAuthenticated) {
    const from = (location.state as { from?: string } | null)?.from || '/'
    return <Navigate to={from} replace />
  }

  const parseAuthError = (err: any): string => {
    const status = err?.response?.status
    const detail = err?.response?.data?.detail

    if (typeof detail === 'string' && detail.length > 0) {
      return detail
    }

    if (status === 401) {
      return 'Invalid email or password.'
    }

    if (status === 409) {
      return 'This email is already registered.'
    }

    if (status === 400) {
      return 'Please check your input and try again.'
    }

    return 'Authentication failed. Please try again.'
  }

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    if (submitting) {
      return
    }

    setSubmitting(true)
    setError(null)

    try {
      if (mode === 'login') {
        await login(email, password)
      } else {
        await register(email, password, displayName || undefined)
      }

      const from = (location.state as { from?: string } | null)?.from || '/'
      navigate(from, { replace: true })
    } catch (err) {
      setError(parseAuthError(err))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="page auth-page" data-testid="auth-page">
      <div className="container">
        <div className="form auth-form">
          <h1>{mode === 'login' ? 'Login' : 'Register'}</h1>
          <p className="subtitle">Use your account to access interview sessions and reports.</p>

          <div className="auth-mode-toggle">
            <button
              type="button"
              className={`btn ${mode === 'login' ? 'btn-primary' : 'btn-secondary'}`}
              data-testid="auth-mode-login"
              onClick={() => {
                setMode('login')
                setError(null)
              }}
              disabled={submitting}
            >
              Login
            </button>
            <button
              type="button"
              className={`btn ${mode === 'register' ? 'btn-primary' : 'btn-secondary'}`}
              data-testid="auth-mode-register"
              onClick={() => {
                setMode('register')
                setError(null)
              }}
              disabled={submitting}
            >
              Register
            </button>
          </div>

          <form onSubmit={onSubmit}>
            <div className="form-group">
              <label htmlFor="auth-email">Email</label>
              <input
                id="auth-email"
                data-testid="auth-email-input"
                type="email"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                required
                autoComplete="email"
              />
            </div>

            <div className="form-group">
              <label htmlFor="auth-password">Password</label>
              <input
                id="auth-password"
                data-testid="auth-password-input"
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                required
                minLength={8}
                autoComplete={mode === 'login' ? 'current-password' : 'new-password'}
              />
            </div>

            {mode === 'register' && (
              <div className="form-group">
                <label htmlFor="auth-display-name">Display Name (optional)</label>
                <input
                  id="auth-display-name"
                  data-testid="auth-display-name-input"
                  type="text"
                  value={displayName}
                  onChange={(e) => setDisplayName(e.target.value)}
                  maxLength={120}
                  autoComplete="name"
                />
              </div>
            )}

            {error && <p className="reports-inline-error">{error}</p>}

            <button type="submit" className="btn btn-primary auth-submit" data-testid="auth-submit-button" disabled={submitting}>
              {submitting ? 'Please wait...' : mode === 'login' ? 'Login' : 'Register'}
            </button>
          </form>
        </div>
      </div>
    </div>
  )
}

export default AuthPage
