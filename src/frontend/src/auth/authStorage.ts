export type AppUserRole = 'User' | 'Admin'

export interface StoredAuthUser {
  userId: string
  email: string
  role?: AppUserRole
}

const TOKEN_KEY = 'interviewcoach.auth.token'
const USER_KEY = 'interviewcoach.auth.user'

export function getToken(): string | null {
  try {
    return window.localStorage.getItem(TOKEN_KEY)
  } catch {
    return null
  }
}

export function setToken(token: string): void {
  try {
    window.localStorage.setItem(TOKEN_KEY, token)
  } catch {
    // no-op
  }
}

export function clearToken(): void {
  try {
    window.localStorage.removeItem(TOKEN_KEY)
  } catch {
    // no-op
  }
}

function normalizeRole(role: unknown): AppUserRole | undefined {
  if (typeof role !== 'string') {
    return undefined
  }

  if (role === 'Admin') {
    return 'Admin'
  }

  if (role === 'User') {
    return 'User'
  }

  return undefined
}

export function getUser(): StoredAuthUser | null {
  try {
    const raw = window.localStorage.getItem(USER_KEY)
    if (!raw) {
      return null
    }

    const parsed = JSON.parse(raw) as Partial<StoredAuthUser>
    if (typeof parsed.userId !== 'string' || typeof parsed.email !== 'string') {
      return null
    }

    return {
      userId: parsed.userId,
      email: parsed.email,
      role: normalizeRole(parsed.role)
    }
  } catch {
    return null
  }
}

export function setUser(user: StoredAuthUser): void {
  try {
    window.localStorage.setItem(USER_KEY, JSON.stringify(user))
  } catch {
    // no-op
  }
}

export function clearUser(): void {
  try {
    window.localStorage.removeItem(USER_KEY)
  } catch {
    // no-op
  }
}

export function clearAuthStorage(): void {
  clearToken()
  clearUser()
}
