import { BrowserRouter as Router, Routes, Route } from 'react-router-dom'
import Home from './pages/Home'
import InterviewSession from './pages/InterviewSession'
import OfflineAnalyze from './pages/OfflineAnalyze'
import Report from './pages/Report'
import ReportsList from './pages/ReportsList'
import AuthPage from './pages/AuthPage'
import AdminPage from './pages/AdminPage'
import TopNav from './components/TopNav'
import { AuthProvider } from './auth/AuthContext'
import ProtectedRoute from './auth/ProtectedRoute'
import AdminRoute from './auth/AdminRoute'
import './App.css'

function App() {
  return (
    <Router>
      <AuthProvider>
        <div className="app">
          <TopNav />
          <Routes>
            <Route path="/auth" element={<AuthPage />} />
            <Route element={<ProtectedRoute />}>
              <Route path="/" element={<Home />} />
              <Route path="/reports" element={<ReportsList />} />
              <Route path="/interview/:sessionId" element={<InterviewSession />} />
              <Route path="/offline" element={<OfflineAnalyze />} />
              <Route path="/report/:sessionId" element={<Report />} />
            </Route>
            <Route element={<AdminRoute />}>
              <Route path="/admin" element={<AdminPage />} />
            </Route>
          </Routes>
        </div>
      </AuthProvider>
    </Router>
  )
}

export default App
