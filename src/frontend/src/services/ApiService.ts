import axios from 'axios'

const API_BASE_URL = import.meta.env.VITE_API_URL || 'http://localhost:5000/api'
const SPEECH_SERVICE_URL = import.meta.env.VITE_SPEECH_URL || 'http://localhost:8000'

export interface LiveAnalysisRequest {
  sessionId: string
  windowSec: number
  role: string
  questionPrompt: string
  videoMetrics: {
    eyeContactAvg: number
    headStabilityAvg: number
    postureAvg: number
    fidgetAvg: number
    eyeOpennessAvg: number
    blinkCountWindow: number
    emotionDistribution: Record<string, number>
  }
}

export interface LiveAnalysisResponse {
  summary: string
  risks: string[]
  suggestions: string[]
  confidence: number
  model: string
  timestamp: string
}

class ApiService {
  // Session endpoints
  async createSession(role: string, language: string) {
    const response = await axios.post(`${API_BASE_URL}/sessions`, {
      role,
      language
    })
    return response.data
  }

  async getSession(sessionId: string) {
    const response = await axios.get(`${API_BASE_URL}/sessions/${sessionId}`)
    return response.data
  }

  async getRecentSessions(limit: number = 30) {
    const response = await axios.get(`${API_BASE_URL}/sessions?limit=${limit}`)
    return response.data || []
  }

  // Question endpoints
  async seedQuestions(sessionId: string) {
    const response = await axios.post(`${API_BASE_URL}/sessions/${sessionId}/questions`)
    return response.data || []
  }

  async getQuestions(sessionId: string) {
    try {
      const response = await axios.get(`${API_BASE_URL}/sessions/${sessionId}/questions`)
      return response.data || []
    } catch {
      return []
    }
  }

  // Metrics endpoints
  async postMetrics(sessionId: string, metrics: any[]) {
    const response = await axios.post(
      `${API_BASE_URL}/sessions/${sessionId}/metrics`,
      { events: metrics }
    )
    return response.data
  }

  // Speech-to-text
  async transcribeAudio(formData: FormData, language: string = 'tr') {
    const response = await axios.post(
      `${SPEECH_SERVICE_URL}/transcribe?language=${language}&compute_stats=true`,
      formData,
      {
        headers: { 'Content-Type': 'multipart/form-data' },
        timeout: 60000
      }
    )
    return response.data
  }

  // Transcript storage
  async storeTranscript(sessionId: string, transcriptData: any) {
    const response = await axios.post(
      `${API_BASE_URL}/sessions/${sessionId}/transcript`,
      transcriptData
    )
    return response.data
  }

  // Finalize session
  async finalizeSession(sessionId: string) {
    const response = await axios.post(
      `${API_BASE_URL}/sessions/${sessionId}/finalize`
    )
    return response.data
  }

  // Get report
  async getReport(sessionId: string) {
    const response = await axios.get(
      `${API_BASE_URL}/sessions/${sessionId}/report`
    )
    return response.data
  }

  // Config
  async getConfig() {
    try {
      const response = await axios.get(`${API_BASE_URL}/config`)
      return response.data
    } catch {
      return {}
    }
  }

  async analyzeLiveWindow(payload: LiveAnalysisRequest): Promise<LiveAnalysisResponse> {
    const response = await axios.post(`${API_BASE_URL}/analysis/live-window`, payload, { timeout: 15000 })
    return response.data
  }
}

export default new ApiService()

