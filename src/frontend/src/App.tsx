import { BrowserRouter as Router, Routes, Route } from 'react-router-dom'
import Home from './pages/Home'
import InterviewSession from './pages/InterviewSession'
import OfflineAnalyze from './pages/OfflineAnalyze'
import Report from './pages/Report'
import ReportsList from './pages/ReportsList'
import TopNav from './components/TopNav'
import './App.css'

function App() {
  return (
    <Router>
      <div className="app">
        <TopNav />
        <Routes>
          <Route path="/" element={<Home />} />
          <Route path="/reports" element={<ReportsList />} />
          <Route path="/interview/:sessionId" element={<InterviewSession />} />
          <Route path="/offline" element={<OfflineAnalyze />} />
          <Route path="/report/:sessionId" element={<Report />} />
        </Routes>
      </div>
    </Router>
  )
}

export default App
