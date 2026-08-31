import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { UserProfilePublic } from '@/lib/profile/public-profile-types'

const API = 'http://api.test'

const sampleProfile: UserProfilePublic = {
  id: 'user-1',
  username: 'alice',
  bio: 'Hello',
  avatarUrl: null,
  isPublicProfile: true,
  createdAt: '2024-01-01T00:00:00.000Z',
  socialLinks: [],
}

const cacheMock = vi.hoisted(() =>
  vi.fn((fn: (username: string) => Promise<UserProfilePublic | null>) => {
    const store = new Map<string, Promise<UserProfilePublic | null>>()
    return (username: string) => {
      const cached = store.get(username)
      if (cached) {
        return cached
      }
      const result = fn(username)
      store.set(username, result)
      return result
    }
  }),
)

vi.mock('react', () => ({
  cache: cacheMock,
}))

describe('fetchPublicProfileByUsername', () => {
  beforeEach(() => {
    vi.stubEnv('ASSETBLOCK_API_BASE_URL', API)
    vi.stubEnv('NEXT_PUBLIC_API_BASE_URL', API)
    vi.resetModules()
    cacheMock.mockClear()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    vi.unstubAllEnvs()
  })

  it('wraps the loader with cache and deduplicates within one request', async () => {
    const fetchMock = vi.fn(async () => {
      return new Response(JSON.stringify(sampleProfile), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    })
    vi.stubGlobal('fetch', fetchMock)

    const { fetchPublicProfileByUsername } = await import('@/lib/server/user-profile-server')

    expect(cacheMock).toHaveBeenCalledOnce()

    const [first, second] = await Promise.all([
      fetchPublicProfileByUsername('alice'),
      fetchPublicProfileByUsername('alice'),
    ])

    expect(first).toEqual(sampleProfile)
    expect(second).toEqual(sampleProfile)
    expect(fetchMock).toHaveBeenCalledWith(
      `${API}/api/users/alice`,
      expect.objectContaining({ cache: 'no-store' }),
    )
  })
})
