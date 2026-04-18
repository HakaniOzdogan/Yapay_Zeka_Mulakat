import { createContext, ReactNode, useContext, useEffect, useMemo, useState } from 'react'
import ApiService, { AuthResponse } from '../services/ApiService'
import {
  AppUserRole,
  clearAuthStorage,
  getToken,
  getUser,
  setToken as persistToken,
  setUser as persistUser,
  StoredAuthUser
} from './authStorage'

interface AuthContextValue {
  isAuthenticated: boolean
  isAdmin: boolean
  token: string | null
  user: StoredAuthUser | null
  loading: boolean
  login: (email: string, password: string) => Promise<void>
  register: (email: string, password: string, displayName?: string) => Promise<void>
  logout: () => void
}

const AuthContext = createContext<AuthContextValue | undefined>(undefined)

function decodeJwtRole(token: string): AppUserRole | undefined {
  try {
    const parts = token.split('.')
    if (parts.length < 2) {
      return undefined
    }

    const payload = parts[1]
      .replace(/-/g, '+')
      .replace(/_/g, '/')
      .padEnd(Math.ceil(parts[1].length / 4) * 4, '=')

    const decoded = atob(payload)
    const claims = JSON.parse(decoded) as Record<string, unknown>

    const roleClaim = claims.role ?? claims['http://schemas.microsoft.com/ws/2008/06/identity/claims/role']
    if (roleClaim === 'Admin') {
      return 'Admin'
    }
    if (roleClaim === 'User') {
      return 'User'
    }

    return undefined
  } catch {
    return undefined
  }
}

function buildStoredUser(result: AuthResponse): StoredAuthUser {
  return {
    userId: result.userId,
    email: result.email,
    role: decodeJwtRole(result.token)
  }
}

function AuthProvider({ children }: { children: ReactNode }) {
  const [token, setToken] = useState<string | null>(null)
  const [user, setUser] = useState<StoredAuthUser | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    const storedToken = getToken()
    const storedUser = getUser()

    if (storedToken) {
      ApiService.setAuthToken(storedToken)
      setToken(storedToken)
    } else {
      ApiService.setAuthToken(null)
    }

    setUser(storedUser)
    setLoading(false)
  }, [])

  const logout = () => {
    clearAuthStorage()
    ApiService.setAuthToken(null)
    setToken(null)
    setUser(null)
  }

  useEffect(() => {
    ApiService.setUnauthorizedHandler(() => {
      logout()
      if (window.location.pathname !== '/auth') {
        window.location.assign('/auth')
      }
    })

    return () => {
      ApiService.setUnauthorizedHandler(null)
    }
  }, [])

  const login = async (email: string, password: string) => {
    const result = await ApiService.login(email, password)
    const nextUser = buildStoredUser(result)

    persistToken(result.token)
    persistUser(nextUser)
    ApiService.setAuthToken(result.token)
    setToken(result.token)
    setUser(nextUser)
  }

  const register = async (email: string, password: string, displayName?: string) => {
    const result = await ApiService.register(email, password, displayName)
    const nextUser = buildStoredUser(result)

    persistToken(result.token)
    persistUser(nextUser)
    ApiService.setAuthToken(result.token)
    setToken(result.token)
    setUser(nextUser)
  }

  const value = useMemo<AuthContextValue>(() => ({
    isAuthenticated: !!token,
    isAdmin: user?.role === 'Admin',
    token,
    user,
    loading,
    login,
    register,
    logout
  }), [token, user, loading])

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

function useAuth(): AuthContextValue {
  const context = useContext(AuthContext)
  if (!context) {
    throw new Error('useAuth must be used within AuthProvider')
  }

  return context
}

export { AuthProvider, useAuth }
