import { afterEach, describe, expect, it, vi } from 'vitest'
import { GET } from './route'
import { AUTH_COOKIE_ACCESS, AUTH_COOKIE_REFRESH } from '@/lib/auth/constants'
import { createMemoryCookieStore, makeJwt } from '@/test/cookie-store'

const cookieStore = createMemoryCookieStore()

vi.mock('next/headers', () => ({
  cookies: async () => cookieStore,
}))

vi.mock('server-only', () => ({}))

describe('GET /api/auth/signalr-access', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    cookieStore.delete(AUTH_COOKIE_ACCESS)
    cookieStore.delete(AUTH_COOKIE_REFRESH)
    cookieStore.setCalls.length = 0
  })

  it('returns canonical 401 ProblemDetails when unauthenticated and no refresh token', async () => {
    const res = await GET()
    expect(res.status).toBe(401)
    expect(res.headers.get('Content-Type')).toBe('application/problem+json')

    const body = await res.json()
    expect(body).toEqual({
      type: 'urn:assetblock:error:ERR_UNAUTHORIZED',
      status: 401,
      title: 'Unauthorized',
      detail: 'Unauthorized',
      code: 'ERR_UNAUTHORIZED',
      traceId: expect.any(String),
    })
  })

  it('returns access token from cookie when authenticated', async () => {
    const token = makeJwt(Math.floor(Date.now() / 1000) + 3600)
    cookieStore.set(AUTH_COOKIE_ACCESS, token)

    const res = await GET()
    expect(res.status).toBe(200)
    expect(res.headers.get('Cache-Control')).toContain('no-store')

    const body = await res.json()
    expect(body).toEqual({ accessToken: token })
  })

  it('attempts session refresh and returns refreshed access token', async () => {
    const rotated = makeJwt(Math.floor(Date.now() / 1000) + 3600)
    cookieStore.set(AUTH_COOKIE_REFRESH, 'valid-refresh-token')

    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(
            JSON.stringify({
              accessToken: rotated,
              refreshToken: 'new-refresh-token',
              accessExpiresAt: new Date(Date.now() + 3600_000).toISOString(),
              refreshExpiresAt: new Date(Date.now() + 86400_000).toISOString(),
            }),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          ),
      ),
    )

    const res = await GET()
    expect(res.status).toBe(200)
    const body = await res.json()
    expect(body).toEqual({ accessToken: rotated })
  })
})
