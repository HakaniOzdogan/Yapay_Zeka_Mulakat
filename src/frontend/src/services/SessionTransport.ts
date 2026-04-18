import { AxiosError } from 'axios'
import { apiHttpClient } from './ApiService'

export interface SessionTransportEvent {
  clientEventId: string
  tsMs: number
  source: string
  type: string
  payload: unknown
}

export interface SessionTransportStats {
  queued: number
  sent: number
  dropped: number
  failedBatches: number
  lastError?: string
}

export interface SessionTransportConfig {
  apiBaseUrl: string
  sessionId: string
  flushIntervalMs?: number
  maxBatchSize?: number
  maxQueue?: number
  maxRetries?: number
}

export class SessionTransport {
  private readonly apiBaseUrl: string
  private readonly sessionId: string
  private readonly flushIntervalMs: number
  private readonly maxBatchSize: number
  private readonly maxQueue: number
  private readonly maxRetries: number

  private readonly queue: SessionTransportEvent[] = []
  private timer: ReturnType<typeof setInterval> | null = null
  private flushing = false
  private stopping = false

  private sent = 0
  private dropped = 0
  private failedBatches = 0
  private lastError?: string

  constructor({
    apiBaseUrl,
    sessionId,
    flushIntervalMs = 500,
    maxBatchSize = 50,
    maxQueue = 500,
    maxRetries = 3
  }: SessionTransportConfig) {
    this.apiBaseUrl = apiBaseUrl.replace(/\/$/, '')
    this.sessionId = sessionId
    this.flushIntervalMs = flushIntervalMs
    this.maxBatchSize = maxBatchSize
    this.maxQueue = maxQueue
    this.maxRetries = maxRetries
  }

  start(): void {
    if (this.timer) return

    this.stopping = false
    this.timer = setInterval(() => {
      void this.flushOnce()
    }, this.flushIntervalMs)
  }

  enqueueEvent(event: SessionTransportEvent): void {
    if (this.stopping) return

    this.queue.push(event)
    while (this.queue.length > this.maxQueue) {
      this.queue.shift()
      this.dropped += 1
    }
  }

  async stop({ flush }: { flush: boolean }): Promise<void> {
    this.stopping = true

    if (this.timer) {
      clearInterval(this.timer)
      this.timer = null
    }

    if (!flush) {
      return
    }

    await this.flushAll()
  }

  getStats(): SessionTransportStats {
    return {
      queued: this.queue.length,
      sent: this.sent,
      dropped: this.dropped,
      failedBatches: this.failedBatches,
      lastError: this.lastError
    }
  }

  private async flushAll(): Promise<void> {
    while (this.queue.length > 0) {
      await this.flushOnce()
      if (this.flushing) {
        await this.wait(20)
      }
    }
  }

  private async flushOnce(): Promise<void> {
    if (this.flushing) return
    if (this.queue.length === 0) return

    this.flushing = true

    const batch = this.queue.splice(0, this.maxBatchSize)
    const result = await this.sendWithRetry(batch)

    if (result.ok) {
      this.sent += batch.length
      this.lastError = undefined
    } else {
      this.failedBatches += 1
      this.lastError = result.error
    }

    this.flushing = false
  }

  private async sendWithRetry(batch: SessionTransportEvent[]): Promise<{ ok: boolean; error?: string }> {
    const delays = [250, 500, 1000]

    for (let attempt = 0; attempt <= this.maxRetries; attempt++) {
      try {
        await apiHttpClient.post(
          `${this.apiBaseUrl}/sessions/${this.sessionId}/events/batch`,
          batch,
          { timeout: 10000 }
        )
        return { ok: true }
      } catch (error) {
        const axiosError = error as AxiosError
        const status = axiosError.response?.status

        if (status === 400 || status === 413) {
          return { ok: false, error: `HTTP ${status}` }
        }

        if (attempt >= this.maxRetries) {
          return {
            ok: false,
            error: status ? `HTTP ${status}` : axiosError.message
          }
        }

        const backoffMs = delays[Math.min(attempt, delays.length - 1)]
        await this.wait(backoffMs)
      }
    }

    return { ok: false, error: 'retry_exhausted' }
  }

  private wait(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms))
  }
}
