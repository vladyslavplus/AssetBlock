import type { SessionResponse, SessionUser } from '@/lib/auth/auth-types'

export const authKeys = {
  all: ['auth'] as const,
  session: () => [...authKeys.all, 'session'] as const,
}

export async function fetchSessionUser(): Promise<SessionUser | null> {
  const res = await fetch('/api/auth/session', { cache: 'no-store' })
  if (!res.ok) {
    return null
  }
  const body = (await res.json()) as SessionResponse
  return body.user ?? null
}
