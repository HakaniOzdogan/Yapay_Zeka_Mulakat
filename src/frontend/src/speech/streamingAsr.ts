/**
 * Speech service sağlık kontrol yardımcıları.
 * WebSocket streaming kaldırıldı — yalnızca batch HTTP transkripsiyon kullanılmaktadır.
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
      return { ready: false, reachable: false, reason: 'unreachable', message: 'Ses servisine ulaşılamıyor.' }
    }
  } catch {
    return { ready: false, reachable: false, reason: 'unreachable', message: 'Ses servisine ulaşılamıyor.' }
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
        reason === 'startup_failed' ? (details.failureDetail || 'Ses modeli başlatılamadı.')
        : reason === 'at_capacity' ? 'Ses servisi kapasitede.'
        : 'Ses modeli yükleniyor, lütfen bekleyin.'

      return { ready: false, reachable: true, reason, message, details }
    }

    return { ready: false, reachable: true, reason: 'startup_failed', message: `Ses servisi hazırlık kontrolü başarısız (${res.status}).`, details }
  } catch {
    return { ready: false, reachable: true, reason: 'model_loading', message: 'Ses modeli yükleniyor, lütfen bekleyin.' }
  }
}

export function getSpeechReadinessMessage(reason: SpeechReadinessReason, detail?: string | null): string | null {
  switch (reason) {
    case 'ready': return null
    case 'model_loading': return 'Ses modeli yükleniyor, lütfen bekleyin.'
    case 'at_capacity': return 'Ses servisi kapasitede.'
    case 'unreachable': return 'Ses servisine ulaşılamıyor.'
    case 'startup_failed': return detail || 'Ses modeli başlatılamadı.'
    default: return 'Ses servisi hazır değil.'
  }
}

export function getSpeechRetryNotice(reason: SpeechReadinessReason): string | null {
  switch (reason) {
    case 'ready': return null
    case 'model_loading': return 'Ses modeli yükleniyor...'
    case 'at_capacity': return 'Ses servisi kapasitede, lütfen bekleyin.'
    case 'unreachable': return 'Ses servisine ulaşılamıyor.'
    case 'startup_failed': return 'Ses modeli başlatılamadı.'
    default: return null
  }
}

export function getSpeechModelLabel(ready: boolean | null, reason: SpeechReadinessReason | null): string {
  if (ready === true || reason === 'ready') return 'Hazır'
  if (reason === 'startup_failed') return 'Başlatma hatası'
  if (reason === 'at_capacity') return 'Dolu'
  if (reason === 'unreachable') return 'Ulaşılamıyor'
  if (reason === 'model_loading' || ready === false) return 'Yükleniyor'
  return 'Kontrol ediliyor...'
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
