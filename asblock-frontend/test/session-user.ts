import type { SessionUser } from '@/lib/auth/auth-types'

export function verifiedSeller(overrides: Partial<SessionUser> = {}): SessionUser {
  return {
    id: '11111111-1111-4111-8111-111111111111',
    username: 'seller',
    role: 'User',
    emailVerifiedAt: '2026-01-01T00:00:00.000Z',
    avatarUrl: null,
    bio: null,
    isPublicProfile: true,
    createdAt: '2026-01-01T00:00:00.000Z',
    socialLinks: [],
    ...overrides,
  }
}
