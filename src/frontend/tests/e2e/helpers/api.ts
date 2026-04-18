import { request } from '@playwright/test'

interface AuthResult {
  userId: string
  email: string
  token: string
}

export interface ApiSessionFixture {
  sessionId: string
  token: string
  email: string
  password: string
}

function resolveSessionId(payload: any): string {
  const id = payload?.sessionId ?? payload?.id
  if (typeof id !== 'string' || id.length === 0) {
    throw new Error('Session id not found in create session response.')
  }

  return id
}

export async function ensureUserToken(apiBaseUrl: string, email: string, password: string): Promise<AuthResult> {
  const context = await request.newContext({
    baseURL: apiBaseUrl,
    extraHTTPHeaders: {
      'Content-Type': 'application/json'
    }
  })

  try {
    const registerRes = await context.post('/auth/register', {
      data: { email, password, displayName: 'E2E User' }
    })

    if (registerRes.ok()) {
      return (await registerRes.json()) as AuthResult
    }

    if (registerRes.status() !== 409) {
      const text = await registerRes.text()
      throw new Error(`Register failed (${registerRes.status()}): ${text}`)
    }

    const loginRes = await context.post('/auth/login', {
      data: { email, password }
    })

    if (!loginRes.ok()) {
      const text = await loginRes.text()
      throw new Error(`Login failed (${loginRes.status()}): ${text}`)
    }

    return (await loginRes.json()) as AuthResult
  } finally {
    await context.dispose()
  }
}

export async function createSessionFixture(
  apiBaseUrl: string,
  email: string,
  password: string
): Promise<ApiSessionFixture> {
  const auth = await ensureUserToken(apiBaseUrl, email, password)

  const context = await request.newContext({
    baseURL: apiBaseUrl,
    extraHTTPHeaders: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${auth.token}`
    }
  })

  try {
    const createSessionRes = await context.post('/sessions', {
      data: { role: 'Software Engineer', language: 'en' }
    })

    if (!createSessionRes.ok()) {
      const text = await createSessionRes.text()
      throw new Error(`Create session failed (${createSessionRes.status()}): ${text}`)
    }

    const sessionPayload = await createSessionRes.json()
    const sessionId = resolveSessionId(sessionPayload)

    const finalizeRes = await context.post(`/sessions/${sessionId}/finalize`)
    if (!finalizeRes.ok()) {
      const text = await finalizeRes.text()
      throw new Error(`Finalize failed (${finalizeRes.status()}): ${text}`)
    }

    return {
      sessionId,
      token: auth.token,
      email,
      password
    }
  } finally {
    await context.dispose()
  }
}
