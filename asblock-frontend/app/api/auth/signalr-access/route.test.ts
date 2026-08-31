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

  it('calls backend signalr-token endpoint and returns hubToken when authenticated', async () => {
    const sessionToken = makeJwt(Math.floor(Date.now() / 1000) + 3600)
    cookieStore.set(AUTH_COOKIE_ACCESS, sessionToken)

    const hubToken = 'hub.jwt.token'
    const expiresAt = new Date(Date.now() + 90_000).toISOString()

    const fetchMock = vi.fn(
      async (url: RequestInfo | URL, init?: RequestInit) => {
        expect(String(url)).toContain('/api/auth/signalr-token')
        expect(init?.method).toBe('POST')
        expect(init?.headers).toBeDefined()
        const headers = new Headers(init?.headers)
        expect(headers.get('Authorization')).toBe(`Bearer ${sessionToken}`)
        return new Response(JSON.stringify({ hubToken, expiresAt }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      },
    )
    vi.stubGlobal('fetch', fetchMock)

    const res = await GET()
    expect(res.status).toBe(200)
    expect(res.headers.get('Cache-Control')).toContain('no-store')

    const body = await res.json()
    expect(body).toEqual({ hubToken, expiresAt })
    expect(body).not.toHaveProperty('accessToken')
  })

  it('preserves 429 status and Retry-After header when rate limited by backend', async () => {
    const sessionToken = makeJwt(Math.floor(Date.now() / 1000) + 3600)
    cookieStore.set(AUTH_COOKIE_ACCESS, sessionToken)

    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify({ error: 'rate limited' }), {
            status: 429,
            headers: {
              'Content-Type': 'application/json',
              'Retry-After': '30',
            },
          }),
      ),
    )

    const res = await GET()
    expect(res.status).toBe(429)
    expect(res.headers.get('Retry-After')).toBe('30')
    const body = await res.json()
    expect(body.code).toBe('ERR_RATE_LIMIT_EXCEEDED')
  })

  it('preserves 504 status when backend request times out', async () => {
    const sessionToken = makeJwt(Math.floor(Date.now() / 1000) + 3600)
    cookieStore.set(AUTH_COOKIE_ACCESS, sessionToken)

    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify({ error: 'gateway timeout' }), {
            status: 504,
            headers: { 'Content-Type': 'application/problem+json' },
          }),
      ),
    )

    const res = await GET()
    expect(res.status).toBe(504)
    const body = await res.json()
    expect(body.code).toBe('ERR_GATEWAY_TIMEOUT')
  })

  it('returns 502 ProblemDetails on network failure', async () => {
    const sessionToken = makeJwt(Math.floor(Date.now() / 1000) + 3600)
    cookieStore.set(AUTH_COOKIE_ACCESS, sessionToken)

    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        throw new Error('Network error')
      }),
    )

    const res = await GET()
    expect(res.status).toBe(502)
    const body = await res.json()
    expect(body.code).toBe('ERR_GATEWAY_ERROR')
  })
})
