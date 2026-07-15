import { cookies } from 'next/headers'
import type { SessionUser } from '@/lib/auth/auth-types'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'

function mapApiUserToSessionUser(data: Record<string, unknown>): SessionUser | null {
  const id = data.id
  const username = data.username
  if (typeof id !== 'string' || typeof username !== 'string') {
    return null
  }
  const socialRaw = data.socialLinks
  const socialLinks = Array.isArray(socialRaw)
    ? socialRaw.map((row) => {
        const r = row as Record<string, unknown>
        return {
          id: String(r.id ?? ''),
          platformName: String(r.platformName ?? ''),
          iconName: String(r.iconName ?? ''),
          url: String(r.url ?? ''),
        }
      })
    : []
  const role = data.role
  return {
    id,
    username,
    role: typeof role === 'string' ? role : role === null ? null : undefined,
    avatarUrl: typeof data.avatarUrl === 'string' ? data.avatarUrl : null,
    bio: typeof data.bio === 'string' ? data.bio : null,
    isPublicProfile: Boolean(data.isPublicProfile),
    createdAt: typeof data.createdAt === 'string' ? data.createdAt : String(data.createdAt ?? ''),
    socialLinks,
  }
}

export async function getServerSessionUser(): Promise<SessionUser | null> {
  const store = await cookies()
  const res = await fetchBackendAuthorized(store, '/api/users/me', { method: 'GET' })
  if (!res.ok) {
    return null
  }
  const json: unknown = await res.json().catch(() => null)
  if (!json || typeof json !== 'object') {
    return null
  }
  return mapApiUserToSessionUser(json as Record<string, unknown>)
}
