import axios, { AxiosInstance } from 'axios'

const API_BASE_URL = import.meta.env.VITE_API_URL || 'http://localhost:8080/api'
const SPEECH_SERVICE_URL = import.meta.env.VITE_SPEECH_URL || 'http://localhost:8000'

export interface AuthResponse {
  userId: string
  email: string
  token: string
}

export interface RetentionRunSummary {
  ranAtUtc: string
  sessionsDeleted: number
  sessionsPruned: number
  metricEventsDeleted: number
  transcriptSegmentsDeleted: number
  feedbackItemsDeleted: number
  scoreCardsDeleted: number
  llmRunsDeleted: number
  details?: string
}

export interface RetentionStatusResponse {
  enabled: boolean
  deleteAfterDays: number
  keepSummariesOnlyAfterDays?: number | null
  runHourUtc: number
  lastRun?: RetentionRunSummary | null
}

export interface AdminUserSummary {
  userId: string
  email: string
  role: 'User' | 'Admin'
  createdAtUtc: string
  isActive: boolean
}

export interface AdminUserRoleUpdateResponse {
  userId: string
  email: string
  role: 'User' | 'Admin'
}

export type BatchCoachingJobStatus = 'Queued' | 'Running' | 'Completed' | 'Failed' | 'Canceled'
export type BatchCoachingJobItemStatus = 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Skipped'

export interface BatchCoachingJobFilter {
  createdFromUtc?: string
  createdToUtc?: string
  language?: string
  roleContains?: string
  onlyIfNoCoach?: boolean
}

export interface BatchCoachingJobOptions {
  force?: boolean
  maxSessions?: number
  parallelism?: number
  stopOnError?: boolean
}

export interface BatchCoachingJobCreateRequest {
  sessionIds?: string[]
  filters?: BatchCoachingJobFilter
  options?: BatchCoachingJobOptions
}

export interface BatchCoachingJobCreateResponse {
  jobId: string
  status?: BatchCoachingJobStatus | string
  totalSessions?: number
}

export interface BatchCoachingJobSummary {
  jobId: string
  status?: BatchCoachingJobStatus | string
  createdAtUtc?: string
  startedAtUtc?: string | null
  completedAtUtc?: string | null
  totalSessions?: number
  processedSessions?: number
  succeededSessions?: number
  failedSessions?: number
  skippedSessions?: number
  progressPercent?: number | null
  lastError?: string | null
}

export interface BatchCoachingJobDetails extends BatchCoachingJobSummary {
  createdByUserId?: string | null
  filters?: BatchCoachingJobFilter | null
  options?: BatchCoachingJobOptions | null
  filtersJson?: string | null
  optionsJson?: string | null
}

export interface BatchCoachingJobItem {
  id?: string
  jobId?: string
  sessionId: string
  status?: BatchCoachingJobItemStatus | string
  attempts?: number
  startedAtUtc?: string | null
  completedAtUtc?: string | null
  resultSource?: string | null
  llmRunId?: string | null
  error?: string | null
}

export interface BatchCoachingJobItemsQuery {
  status?: BatchCoachingJobItemStatus | string
  take?: number
  skip?: number
}

export interface BatchCoachingJobItemsResponse {
  items: BatchCoachingJobItem[]
  totalCount?: number
  take?: number
  skip?: number
}

export interface LlmCoachingRubric {
  technical_correctness: number
  depth: number
  structure: number
  clarity: number
  confidence: number
}

export interface LlmCoachingFeedbackItem {
  category: 'vision' | 'audio' | 'content' | 'structure'
  severity: number
  title: string
  evidence: string
  time_range_ms: [number, number]
  suggestion: string
  example_phrase: string
}

export interface LlmCoachingDrill {
  title: string
  steps: string[]
  duration_min: number
}

export interface LlmCoachingResponse {
  rubric: LlmCoachingRubric
  overall: number
  feedback: LlmCoachingFeedbackItem[]
  drills: LlmCoachingDrill[]
}

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

export interface MetricEventIngestDto {
  clientEventId: string
  tsMs: number
  source: string
  type: string
  payload: Record<string, unknown>
}

export interface TranscriptSegmentIngestDto {
  clientSegmentId: string
  startMs: number
  endMs: number
  text: string
  confidence?: number
}

export interface DownloadFileResult {
  blob: Blob
  filename: string
}

export interface ScoringProfileConfig {
  weights: {
    eyeContact: number
    posture: number
    fidget: number
    speakingRate: number
    fillerWords: number
  }
  thresholds: {
    speakingRateIdealMinWpm: number
    speakingRateIdealMaxWpm: number
    fillerPerMinMax: number
    eyeContactMin: number
    headJitterMax: number
    fidgetMax: number
    postureMin: number
  }
}

export interface ScoringProfilesResponse {
  defaultProfile: string
  profiles: Record<string, ScoringProfileConfig>
}

export interface ScoreCardPreview {
  eyeContactScore: number
  speakingRateScore: number
  fillerScore: number
  postureScore: number
  overallScore: number
}

export interface ScoringPreviewResponse {
  sessionId: string
  profileName: string
  scoreCardPreview: ScoreCardPreview
  currentStoredScoreCard: ScoreCardPreview | null
}

export interface SessionScoringProfileResponse {
  sessionId: string
  scoringProfile: string
}

export const apiHttpClient: AxiosInstance = axios.create({
  baseURL: API_BASE_URL
})

class ApiService {
  private readonly client: AxiosInstance
  private unauthorizedHandler: (() => void) | null = null

  constructor() {
    this.client = apiHttpClient

    this.client.interceptors.response.use(
      (response) => response,
      (error) => {
        const status = error?.response?.status
        const requestUrl = String(error?.config?.url || '')
        const isAuthEndpoint = requestUrl.includes('/auth/login') || requestUrl.includes('/auth/register')

        if (status === 401 && !isAuthEndpoint) {
          this.unauthorizedHandler?.()
        }

        return Promise.reject(error)
      }
    )
  }

  setUnauthorizedHandler(handler: (() => void) | null): void {
    this.unauthorizedHandler = handler
  }

  setAuthToken(token: string | null): void {
    if (token) {
      this.client.defaults.headers.common.Authorization = `Bearer ${token}`
      axios.defaults.headers.common.Authorization = `Bearer ${token}`
      return
    }

    delete this.client.defaults.headers.common.Authorization
    delete axios.defaults.headers.common.Authorization
  }

  async register(email: string, password: string, displayName?: string): Promise<AuthResponse> {
    const response = await this.client.post('/auth/register', {
      email,
      password,
      displayName
    })

    return response.data as AuthResponse
  }

  async login(email: string, password: string): Promise<AuthResponse> {
    const response = await this.client.post('/auth/login', {
      email,
      password
    })

    return response.data as AuthResponse
  }

  async getRetentionStatus(): Promise<RetentionStatusResponse> {
    const response = await this.client.get('/admin/retention/status')
    return response.data as RetentionStatusResponse
  }

  async runRetention(): Promise<RetentionRunSummary> {
    const response = await this.client.post('/admin/retention/run')
    return response.data as RetentionRunSummary
  }

  async getAdminUsers(take: number = 100): Promise<AdminUserSummary[]> {
    const safeTake = Math.min(100, Math.max(1, take))
    const response = await this.client.get(`/admin/users?take=${safeTake}`)
    return (response.data as AdminUserSummary[]) || []
  }

  async setUserRole(userId: string, role: 'User' | 'Admin'): Promise<AdminUserRoleUpdateResponse> {
    const response = await this.client.post(`/admin/users/${userId}/role`, { role })
    return response.data as AdminUserRoleUpdateResponse
  }

  async createBatchCoachJob(payload: BatchCoachingJobCreateRequest): Promise<BatchCoachingJobCreateResponse> {
    const response = await this.client.post('/admin/llm/batch-coach/jobs', payload)
    return response.data as BatchCoachingJobCreateResponse
  }

  async getBatchCoachJobs(take: number = 20): Promise<BatchCoachingJobSummary[]> {
    const safeTake = Math.min(100, Math.max(1, take))
    const response = await this.client.get(`/admin/llm/batch-coach/jobs?take=${safeTake}`)
    return (response.data as BatchCoachingJobSummary[]) || []
  }

  async getBatchCoachJob(jobId: string): Promise<BatchCoachingJobDetails> {
    const response = await this.client.get(`/admin/llm/batch-coach/jobs/${jobId}`)
    return response.data as BatchCoachingJobDetails
  }

  async getBatchCoachJobItems(jobId: string, query?: BatchCoachingJobItemsQuery): Promise<BatchCoachingJobItemsResponse> {
    const params = new URLSearchParams()

    if (query?.status) {
      params.set('status', query.status)
    }

    if (typeof query?.take === 'number') {
      params.set('take', String(query.take))
    }

    if (typeof query?.skip === 'number') {
      params.set('skip', String(query.skip))
    }

    const queryString = params.toString()
    const path = queryString.length > 0
      ? `/admin/llm/batch-coach/jobs/${jobId}/items?${queryString}`
      : `/admin/llm/batch-coach/jobs/${jobId}/items`

    const response = await this.client.get(path)
    return response.data as BatchCoachingJobItemsResponse
  }

  async cancelBatchCoachJob(jobId: string): Promise<BatchCoachingJobDetails> {
    const response = await this.client.post(`/admin/llm/batch-coach/jobs/${jobId}/cancel`)
    return response.data as BatchCoachingJobDetails
  }

  private tryParseCoachingPayload(input: unknown): LlmCoachingResponse | null {
    if (!input) {
      return null
    }

    if (typeof input === 'string') {
      try {
        const parsed = JSON.parse(input)
        return this.tryParseCoachingPayload(parsed)
      } catch {
        return null
      }
    }

    if (typeof input !== 'object') {
      return null
    }

    const value = input as Partial<LlmCoachingResponse>
    if (
      value.rubric &&
      typeof value.overall === 'number' &&
      Array.isArray(value.feedback) &&
      Array.isArray(value.drills)
    ) {
      return value as LlmCoachingResponse
    }

    return null
  }

  private getCoachingFromReportPayload(report: any): LlmCoachingResponse | null {
    const direct = this.tryParseCoachingPayload(report?.llmCoaching)
    if (direct) {
      return direct
    }

    const directJson = this.tryParseCoachingPayload(report?.llmCoachingJson)
    if (directJson) {
      return directJson
    }

    const events = Array.isArray(report?.metricEvents)
      ? report.metricEvents
      : Array.isArray(report?.events)
        ? report.events
        : []

    const llmEvent = events.find((event: any) => event?.type === 'llm_coaching_v1')
    if (!llmEvent) {
      return null
    }

    return (
      this.tryParseCoachingPayload(llmEvent?.payloadJson) ??
      this.tryParseCoachingPayload(llmEvent?.payload) ??
      null
    )
  }

  // Session endpoints
  async createSession(role: string, language: string) {
    const response = await this.client.post('/sessions', {
      role,
      language
    })
    return response.data
  }

  async getSession(sessionId: string) {
    const response = await this.client.get(`/sessions/${sessionId}`)
    return response.data
  }

  async getRecentSessions(limit: number = 30) {
    const response = await this.client.get(`/sessions/recent?take=${limit}`)
    return response.data || []
  }

  async deleteSession(sessionId: string) {
    await this.client.delete(`/sessions/${sessionId}`)
  }

  // Question endpoints
  async seedQuestions(sessionId: string) {
    const response = await this.client.post(`/sessions/${sessionId}/questions`)
    return response.data || []
  }

  async getQuestions(sessionId: string) {
    try {
      const response = await this.client.get(`/sessions/${sessionId}/questions`)
      return response.data || []
    } catch {
      return []
    }
  }

  // Metrics endpoints
  async postMetrics(sessionId: string, metrics: any[]) {
    const response = await this.client.post(
      `/sessions/${sessionId}/metrics`,
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

  async postSessionEventsBatch(sessionId: string, events: MetricEventIngestDto[]) {
    const response = await this.client.post(
      `/sessions/${sessionId}/events/batch`,
      events
    )
    return response.data
  }

  async postTranscriptBatch(sessionId: string, segments: TranscriptSegmentIngestDto[]) {
    const response = await this.client.post(
      `/sessions/${sessionId}/transcript/batch`,
      segments
    )
    return response.data
  }

  // Transcript storage
  async storeTranscript(sessionId: string, transcriptData: any) {
    const response = await this.client.post(
      `/sessions/${sessionId}/transcript`,
      transcriptData
    )
    return response.data
  }

  // Finalize session
  async finalizeSession(sessionId: string) {
    const response = await this.client.post(
      `/sessions/${sessionId}/finalize`
    )
    return response.data
  }

  // Get report
  async getReport(sessionId: string) {
    const response = await this.client.get(`/reports/${sessionId}`)
    return response.data
  }

  async getScoringProfiles(): Promise<ScoringProfilesResponse> {
    const response = await this.client.get('/scoring/profiles')
    return response.data as ScoringProfilesResponse
  }

  async previewScoringProfile(sessionId: string, profileName: string): Promise<ScoringPreviewResponse> {
    const response = await this.client.post(`/sessions/${sessionId}/scoring/preview`, {
      profileName
    })
    return response.data as ScoringPreviewResponse
  }

  async setScoringProfile(sessionId: string, profileName: string): Promise<SessionScoringProfileResponse> {
    const response = await this.client.post(`/sessions/${sessionId}/scoring/profile`, {
      profileName
    })
    return response.data as SessionScoringProfileResponse
  }

  async downloadReportExportJson(sessionId: string): Promise<DownloadFileResult> {
    const response = await this.client.get(`/sessions/${sessionId}/report/export.json`, {
      responseType: 'blob'
    })

    return {
      blob: response.data as Blob,
      filename: this.extractFilename(response.headers?.['content-disposition']) || `interview-report-${sessionId}.json`
    }
  }

  async downloadReportExportMarkdown(sessionId: string): Promise<DownloadFileResult> {
    const response = await this.client.get(`/sessions/${sessionId}/report/export.md`, {
      responseType: 'blob'
    })

    return {
      blob: response.data as Blob,
      filename: this.extractFilename(response.headers?.['content-disposition']) || `interview-report-${sessionId}.md`
    }
  }

  async getLlmCoaching(sessionId: string): Promise<LlmCoachingResponse> {
    const report = await this.getReport(sessionId)
    const cachedFromReport = this.getCoachingFromReportPayload(report)
    if (cachedFromReport) {
      return cachedFromReport
    }

    const response = await this.client.post(
      `/sessions/${sessionId}/llm/coach?force=false`
    )
    return response.data as LlmCoachingResponse
  }

  async generateLlmCoaching(sessionId: string): Promise<LlmCoachingResponse> {
    const response = await this.client.post(
      `/sessions/${sessionId}/llm/coach?force=true`
    )
    return response.data as LlmCoachingResponse
  }

  // Config
  async getConfig() {
    try {
      const response = await this.client.get('/config')
      return response.data
    } catch {
      return {}
    }
  }

  async analyzeLiveWindow(payload: LiveAnalysisRequest): Promise<LiveAnalysisResponse> {
    const response = await this.client.post('/analysis/live-window', payload, { timeout: 15000 })
    return response.data
  }

  private extractFilename(contentDisposition?: string): string | null {
    if (!contentDisposition) {
      return null
    }

    const utf8Match = /filename\*=UTF-8''([^;]+)/i.exec(contentDisposition)
    if (utf8Match?.[1]) {
      try {
        return decodeURIComponent(utf8Match[1].trim())
      } catch {
        return utf8Match[1].trim()
      }
    }

    const plainMatch = /filename="?([^";]+)"?/i.exec(contentDisposition)
    if (plainMatch?.[1]) {
      return plainMatch[1].trim()
    }

    return null
  }
}

export default new ApiService()
