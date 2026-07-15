'use client'

import { useQuery, useQueryClient } from '@tanstack/react-query'
import { createContext, useContext, type ReactNode } from 'react'
import type { SessionUser } from '@/lib/auth/auth-types'
import { authKeys, fetchSessionUser } from '@/lib/auth/auth-query'
import { isAdminRole } from '@/lib/auth/roles'

type AuthStatus = 'loading' | 'anonymous' | 'authenticated'

interface AuthContextValue {
  user: SessionUser | null
  status: AuthStatus
  /** True when session user has backend Admin role (own profile only). */
  isAdmin: boolean
  /** Re-fetch session from BFF (e.g. after login/register). */
  refresh: () => Promise<void>
  logout: () => Promise<void>
}

const AuthContext = createContext<AuthContextValue | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()

  const sessionQuery = useQuery({
    queryKey: authKeys.session(),
    queryFn: fetchSessionUser,
    staleTime: 30 * 1000,
    gcTime: 30 * 60 * 1000,
    retry: 1,
    refetchOnWindowFocus: true,
  })

  const user = sessionQuery.data ?? null
  const status: AuthStatus = sessionQuery.isPending
    ? 'loading'
    : user
      ? 'authenticated'
      : 'anonymous'
  const isAdmin = isAdminRole(user?.role)

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: authKeys.session() })
    await queryClient.refetchQueries({ queryKey: authKeys.session() })
  }

  const logout = async () => {
    await fetch('/api/auth/logout', { method: 'POST' })
    queryClient.clear()
  }

  const value = { user, status, isAdmin, refresh, logout }

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext)
  if (!ctx) {
    throw new Error('useAuth must be used within AuthProvider')
  }
  return ctx
}
