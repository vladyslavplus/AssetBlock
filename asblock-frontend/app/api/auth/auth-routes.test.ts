import { afterEach, describe, expect, it, vi } from 'vitest'

import { POST as loginPost } from '@/app/api/auth/login/route'
import { POST as logoutPost } from '@/app/api/auth/logout/route'
import { POST as refreshPost } from '@/app/api/auth/refresh/route'
import { AUTH_COOKIE_ACCESS, AUTH_COOKIE_REFRESH } from '@/lib/auth/constants'
import { createMemoryCookieStore, makeJwt } from '@/test/cookie-store'

const cookieStore = createMemoryCookieStore()

vi.mock('next/headers', () => ({
  cookies: async () => cookieStore,
}))

vi.mock('server-only', () => ({}))

function loginRequest(body: unknown, origin = 'http://localhost:3000'): Request {
  return new Request('http://localhost:3000/api/auth/login', {
    method: 'POST',
    headers: {
      Origin: origin,
      'Content-Type': 'application/json',
    },
    body: typeof body === 'string' ? body : JSON.stringify(body),
  })
}

describe('auth BFF routes', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    cookieStore.delete(AUTH_COOKIE_ACCESS)
    cookieStore.delete(AUTH_COOKIE_REFRESH)
    cookieStore.setCalls.length = 0
  })

  it('rejects cross-origin login and does not set cookies', async () => {
    const res = await loginPost(
      loginRequest({ email: 'a@b.com', password: 'secret' }, 'https://evil.test'),
    )
    expect(res.status).toBe(403)
    expect(cookieStore.setCalls).toHaveLength(0)
  })

  it('returns a safe 400 for malformed JSON', async () => {
    const res = await loginPost(
      new Request('http://localhost:3000/api/auth/login', {
        method: 'POST',
        headers: { Origin: 'http://localhost:3000', 'Content-Type': 'application/json' },
        body: '{not-json',
      }),
    )
    expect(res.status).toBe(400)
    const json = await res.json()
    expect(JSON.stringify(json)).not.toMatch(/SyntaxError|stack/)
  })

  it('stores tokens only in cookies and returns { ok: true }', async () => {
    const access = makeJwt(Math.floor(Date.now() / 1000) + 3600)
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        return new Response(
          JSON.stringify({
            accessToken: access,
            refreshToken: 'refresh-secret',
            accessExpiresAt: new Date(Date.now() + 3600_000).toISOString(),
            refreshExpiresAt: new Date(Date.now() + 86400_000).toISOString(),
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        )
      }),
    )
    const res = await loginPost(
      loginRequest({ email: 'seller@example.com', password: 'correct-horse' }),
    )
    expect(res.status).toBe(200)
    const body = await res.json()
    expect(body).toEqual({ ok: true })
    expect(JSON.stringify(body)).not.toContain(access)
    expect(JSON.stringify(body)).not.toContain('refresh-secret')
    expect(cookieStore.snapshot()[AUTH_COOKIE_ACCESS]).toBe(access)
    expect(cookieStore.snapshot()[AUTH_COOKIE_REFRESH]).toBe('refresh-secret')
  })

  it('clears cookies on confirmed ERR_AUTH_TOKEN_INVALID and returns 401 ProblemDetails', async () => {
    cookieStore.set(AUTH_COOKIE_ACCESS, 'old-access')
    cookieStore.set(AUTH_COOKIE_REFRESH, 'old-refresh')
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(
            JSON.stringify({
              code: 'ERR_AUTH_TOKEN_INVALID',
              title: 'Unauthorized',
              type: 'urn:assetblock:error:ERR_AUTH_TOKEN_INVALID',
            }),
            { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
          ),
      ),
    )
    const res = await refreshPost(
      new Request('http://localhost:3000/api/auth/refresh', {
        method: 'POST',
        headers: { Origin: 'http://localhost:3000' },
      }),
    )
    expect(res.status).toBe(401)
    expect(res.headers.get('Content-Type')).toBe('application/problem+json')
    const body = await res.json()
    expect(body.type).toBe('urn:assetblock:error:ERR_UNAUTHORIZED')
    expect(body.code).toBe('ERR_UNAUTHORIZED')
    expect(body.status).toBe(401)
    expect(body.title).toBe('Unauthorized')
    expect(body.detail).toBe('Unauthorized')
    expect(body.traceId).toBeDefined()
    expect(JSON.stringify(body)).not.toContain('old-refresh')
    expect(cookieStore.snapshot()[AUTH_COOKIE_ACCESS]).toBeUndefined()
    expect(cookieStore.snapshot()[AUTH_COOKIE_REFRESH]).toBeUndefined()
  })

  it('preserves cookies and Retry-After header on 429 rate limit', async () => {
    cookieStore.set(AUTH_COOKIE_ACCESS, 'old-access')
    cookieStore.set(AUTH_COOKIE_REFRESH, 'old-refresh')
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(
            JSON.stringify({
              code: 'ERR_RATE_LIMIT_EXCEEDED',
              title: 'Too Many Requests',
            }),
            {
              status: 429,
              headers: {
                'Content-Type': 'application/problem+json',
                'Retry-After': '120',
              },
            },
          ),
      ),
    )
    const res = await refreshPost(
      new Request('http://localhost:3000/api/auth/refresh', {
        method: 'POST',
        headers: { Origin: 'http://localhost:3000' },
      }),
    )
    expect(res.status).toBe(429)
    expect(res.headers.get('Retry-After')).toBe('120')
    const body = await res.json()
    expect(body.code).toBe('ERR_RATE_LIMIT_EXCEEDED')
    expect(cookieStore.snapshot()[AUTH_COOKIE_REFRESH]).toBe('old-refresh')
  })

  it('preserves cookies on malformed 200 payload and returns 502', async () => {
    cookieStore.set(AUTH_COOKIE_ACCESS, 'old-access')
    cookieStore.set(AUTH_COOKIE_REFRESH, 'old-refresh')
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify({ malformed: true }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )
    const res = await refreshPost(
      new Request('http://localhost:3000/api/auth/refresh', {
        method: 'POST',
        headers: { Origin: 'http://localhost:3000' },
      }),
    )
    expect(res.status).toBe(502)
    const body = await res.json()
    expect(body.code).toBe('ERR_GATEWAY_ERROR')
    expect(cookieStore.snapshot()[AUTH_COOKIE_REFRESH]).toBe('old-refresh')
  })

  it('preserves cookies on 403 ERR_FORBIDDEN and does not wipe session', async () => {
    cookieStore.set(AUTH_COOKIE_ACCESS, 'old-access')
    cookieStore.set(AUTH_COOKIE_REFRESH, 'old-refresh')
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(
            JSON.stringify({
              code: 'ERR_FORBIDDEN',
              title: 'Forbidden',
            }),
            { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
          ),
      ),
    )
    const res = await refreshPost(
      new Request('http://localhost:3000/api/auth/refresh', {
        method: 'POST',
        headers: { Origin: 'http://localhost:3000' },
      }),
    )
    // 403 without ERR_AUTH_TOKEN_INVALID must not wipe cookies
    expect(res.status).toBe(403)
    expect(cookieStore.snapshot()[AUTH_COOKIE_REFRESH]).toBe('old-refresh')
  })

  it('does not leak untrusted upstream error detail to the browser on 500/400 errors', async () => {
    cookieStore.set(AUTH_COOKIE_ACCESS, 'old-access')
    cookieStore.set(AUTH_COOKIE_REFRESH, 'old-refresh')
    const secretDetail = 'Sensitive DB password connection failed at 10.0.0.1:5432'
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(
            JSON.stringify({
              code: 'ERR_INTERNAL_SERVER_ERROR',
              title: 'Internal Server Error',
              detail: secretDetail,
            }),
            { status: 500, headers: { 'Content-Type': 'application/problem+json' } },
          ),
      ),
    )
    const res = await refreshPost(
      new Request('http://localhost:3000/api/auth/refresh', {
        method: 'POST',
        headers: { Origin: 'http://localhost:3000' },
      }),
    )
    expect(res.status).toBe(502)
    const body = await res.json()
    expect(body.code).toBe('ERR_GATEWAY_ERROR')
    expect(body.detail).toBe('The service response was invalid.')
    expect(JSON.stringify(body)).not.toContain(secretDetail)
    expect(cookieStore.snapshot()[AUTH_COOKIE_REFRESH]).toBe('old-refresh')
  })

  it('logout clears auth cookies and returns ok', async () => {
    cookieStore.set(AUTH_COOKIE_ACCESS, 'access')
    cookieStore.set(AUTH_COOKIE_REFRESH, 'refresh')
    const fetchMock = vi.fn(
      async (_input: RequestInfo | URL, _init?: RequestInit) => new Response(null, { status: 200 }),
    )
    vi.stubGlobal('fetch', fetchMock)
    const res = await logoutPost(
      new Request('http://localhost:3000/api/auth/logout', {
        method: 'POST',
        headers: { Origin: 'http://localhost:3000' },
      }),
    )
    expect(res.status).toBe(200)
    expect(await res.json()).toEqual({ ok: true })
    expect(cookieStore.snapshot()).toEqual({})
    expect(fetchMock).toHaveBeenCalledOnce()
    expect(fetchMock.mock.calls[0]?.[0]).toMatch(/\/api\/auth\/logout$/)
  })

  it('logout clears cookies even when backend logout fails', async () => {
    cookieStore.set(AUTH_COOKIE_ACCESS, 'access')
    cookieStore.set(AUTH_COOKIE_REFRESH, 'refresh')
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(JSON.stringify({ title: 'Server Error' }), { status: 500 })),
    )
    const res = await logoutPost(
      new Request('http://localhost:3000/api/auth/logout', {
        method: 'POST',
        headers: { Origin: 'http://localhost:3000' },
      }),
    )
    expect(res.status).toBe(200)
    expect(await res.json()).toEqual({ ok: true })
    expect(cookieStore.snapshot()).toEqual({})
  })
})
