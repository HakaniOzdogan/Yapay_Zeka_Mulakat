import { NavLink, Link, useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'
import '../styles/pages.css'

function TopNav() {
  const location = useLocation()
  const navigate = useNavigate()
  const { isAuthenticated, isAdmin, user, logout } = useAuth()

  const isAuthPage = location.pathname === '/auth'

  if (isAuthPage) {
    return null
  }

  const onLogout = () => {
    logout()
    navigate('/auth', { replace: true })
  }

  return (
    <header className="top-nav">
      <div className="top-nav-inner">
        <Link to="/" className="top-nav-brand">
          <span className="brand-dot" />
          AI Interview
        </Link>

        <nav className="top-nav-links">
          <NavLink to="/" className={({ isActive }) => `top-nav-link ${isActive ? 'active' : ''}`}>
            Platform
          </NavLink>
          {isAuthenticated && (
            <NavLink to="/reports" className={({ isActive }) => `top-nav-link ${isActive ? 'active' : ''}`}>
              Analytics
            </NavLink>
          )}
          {isAdmin && (
            <NavLink to="/admin" data-testid="nav-admin-link" className={({ isActive }) => `top-nav-link ${isActive ? 'active' : ''}`}>
              Admin
            </NavLink>
          )}
        </nav>

        <div className="top-nav-actions">
          {isAuthenticated ? (
            <>
              <span className="top-nav-user">{user?.email}</span>
              <button type="button" data-testid="logout-button" className="btn btn-secondary top-nav-logout" onClick={onLogout}>
                Logout
              </button>
            </>
          ) : (
            <button type="button" className="btn btn-primary" onClick={() => navigate('/auth')}>
              Sign In
            </button>
          )}
        </div>
      </div>
    </header>
  )
}

export default TopNav
