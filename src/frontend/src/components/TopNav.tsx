import { NavLink, useLocation } from 'react-router-dom'

function TopNav() {
  const location = useLocation()

  const hideOnPrint = location.pathname.startsWith('/report/')

  return (
    <header className={`top-nav ${hideOnPrint ? 'top-nav-report' : ''}`}>
      <div className="top-nav-inner">
        <div className="top-nav-brand">
          <span className="brand-dot" />
          Interview Coach
        </div>

        <nav className="top-nav-links">
          <NavLink to="/" className={({ isActive }) => `top-nav-link ${isActive ? 'active' : ''}`}>
            Ana Sayfa
          </NavLink>
          <NavLink to="/reports" className={({ isActive }) => `top-nav-link ${isActive ? 'active' : ''}`}>
            Gecmis Raporlar
          </NavLink>
        </nav>
      </div>
    </header>
  )
}

export default TopNav
