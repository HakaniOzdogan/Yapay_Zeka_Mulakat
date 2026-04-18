import { Navigate, Outlet, useLocation } from 'react-router-dom'
import { useAuth } from './AuthContext'

function AdminRoute() {
  const location = useLocation()
  const { isAuthenticated, isAdmin, loading } = useAuth()

  if (loading) {
    return (
      <div className="page">
        <div className="container">
          <p className="loading-text">Checking authentication...</p>
        </div>
      </div>
    )
  }

  if (!isAuthenticated) {
    return <Navigate to="/auth" replace state={{ from: location.pathname }} />
  }

  if (!isAdmin) {
    return (
      <div className="page" data-testid="admin-forbidden">
        <div className="container">
          <div className="form" style={{ maxWidth: 520 }}>
            <h2>Forbidden</h2>
            <p className="subtitle" style={{ marginBottom: 0 }}>Admin access required.</p>
          </div>
        </div>
      </div>
    )
  }

  return <Outlet />
}

export default AdminRoute
