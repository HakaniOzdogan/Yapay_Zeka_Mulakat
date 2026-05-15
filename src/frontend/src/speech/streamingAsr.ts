/**
 * Speech service health check helpers.
 * WebSocket streaming removed — batch HTTP transcription only.
 */

export type SpeechReadinessReason =
  | 'ready'
  | 'model_loading'
  | 'at_capacity'
  | 'unreachable'
  | 'startup_failed'

export interface StreamingAsrReadiness {
  ready: boolean
  reachable: boolean
  reason: SpeechReadinessReason
  message: string | null
  details?: {
    status?: string
    modelLoaded?: boolean
    failureReason?: string
    failureDetail?: string
    startupState?: string
    activeSessions?: number
    maxConcurrentSessions?: number
    uptimeSec?: number
  }
}

export interface SpeechDiagnostics {
  model: string
  asr_backend?: string
  model_ready: boolean
  compute_type: string
  device: string
  audio_input_contract: string
  live_input_sample_rate: number
  live_input_channels: number
  live_input_chunk_ms: number
  vad_backend: string
  silero_available: boolean
  strict_quality_mode: boolean
  active_sessions: number
  max_sessions: number
  avg_transcribe_latency_ms: number
  p95_transcribe_latency_ms: number
  total_final_segments: number
  total_partial_segments: number
  total_connections: number
  total_errors: number
  vad_voiced_chunks: number
  vad_rejected_chunks: number
  filtered_decode_results_total: number
  empty_decode_results_total: number
  duplicate_finals_suppressed_total: number
  uptime_sec: number
}

function toHttpUrl(baseUrl: string): string {
  const withProto = baseUrl.startsWith('http://') || baseUrl.startsWith('https://')
    ? baseUrl
    : baseUrl.replace(/^ws:\/\//i, 'http://').replace(/^wss:\/\//i, 'https://')
  return withProto.replace(/\/$/, '')
}

export async function getStreamingAsrReadiness(baseUrl: string): Promise<StreamingAsrReadiness> {
  const root = toHttpUrl(baseUrl)

  try {
    const healthRes = await fetch(`${root}/health`, { method: 'GET', cache: 'no-store' })
    if (!healthRes.ok) {
      return { ready: false, reachable: false, reason: 'unreachable', message: 'Speech service unreachable.' }
    }
  } catch {
    return { ready: false, reachable: false, reason: 'unreachable', message: 'Speech service unreachable.' }
  }

  try {
    const res = await fetch(`${root}/health/ready`, { method: 'GET', cache: 'no-store' })
    let payload: any = null
    try { payload = await res.json() } catch { /* no-op */ }

    const details = {
      status: typeof payload?.status === 'string' ? payload.status : undefined,
      modelLoaded: typeof payload?.modelLoaded === 'boolean' ? payload.modelLoaded : undefined,
      failureReason: typeof payload?.failureReason === 'string' ? payload.failureReason : undefined,
      failureDetail: typeof payload?.failureDetail === 'string' ? payload.failureDetail : undefined,
      startupState: typeof payload?.startupState === 'string' ? payload.startupState : undefined,
    }

    if (res.ok) {
      return { ready: true, reachable: true, reason: 'ready', message: null, details }
    }

    if (res.status === 503) {
      const reason: SpeechReadinessReason =
        details.failureReason === 'startup_failed' ? 'startup_failed'
        : details.status === 'at_capacity' ? 'at_capacity'
        : 'model_loading'

      const message =
        reason === 'startup_failed' ? (details.failureDetail || 'Speech model failed to initialize.')
        : reason === 'at_capacity' ? 'Speech service at capacity.'
        : 'Speech model loading, please wait.'

      return { ready: false, reachable: true, reason, message, details }
    }

    return { ready: false, reachable: true, reason: 'startup_failed', message: `Speech service readiness check failed (${res.status}).`, details }
  } catch {
    return { ready: false, reachable: true, reason: 'model_loading', message: 'Speech model loading, please wait.' }
  }
}

export function getSpeechReadinessMessage(reason: SpeechReadinessReason, detail?: string | null): string | null {
  switch (reason) {
    case 'ready': return null
    case 'model_loading': return 'Speech model loading, please wait.'
    case 'at_capacity': return 'Speech service at capacity.'
    case 'unreachable': return 'Speech service unreachable.'
    case 'startup_failed': return detail || 'Speech model failed to initialize.'
    default: return 'Speech service not ready.'
  }
}

export function getSpeechRetryNotice(reason: SpeechReadinessReason): string | null {
  switch (reason) {
    case 'ready': return null
    case 'model_loading': return 'Loading speech model...'
    case 'at_capacity': return 'Speech service at capacity, please wait.'
    case 'unreachable': return 'Speech service unreachable.'
    case 'startup_failed': return 'Speech model failed to initialize.'
    default: return null
  }
}

export function getSpeechModelLabel(ready: boolean | null, reason: SpeechReadinessReason | null): string {
  if (ready === true || reason === 'ready') return 'Ready'
  if (reason === 'startup_failed') return 'Init error'
  if (reason === 'at_capacity') return 'Full'
  if (reason === 'unreachable') return 'Unreachable'
  if (reason === 'model_loading' || ready === false) return 'Loading'
  return 'Checking...'
}

export async function fetchSpeechDiagnostics(baseUrl: string): Promise<SpeechDiagnostics | null> {
  const root = toHttpUrl(baseUrl)
  try {
    const res = await fetch(`${root}/health/diagnostics`, { method: 'GET', cache: 'no-store' })
    if (!res.ok) return null
    return await res.json()
  } catch {
    return null
  }
}
