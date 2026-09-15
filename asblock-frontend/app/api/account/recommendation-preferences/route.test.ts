import { afterEach, describe, expect, it, vi } from 'vitest'
import { GET, PATCH } from './route'
import { AUTH_COOKIE_ACCESS, AUTH_COOKIE_REFRESH } from '@/lib/auth/constants'
import { createMemoryCookieStore, makeJwt } from '@/test/cookie-store'

const cookieStore = createMemoryCookieStore()

vi.mock('next/headers', () => ({
  cookies: async () => cookieStore,
}))

vi.mock('server-only', () => ({}))

function authedRequest(url: string, method: string, body?: unknown): Request {
  return new Request(url, {
    method,
    headers: {
      Origin: 'http://localhost:3000',
      'Content-Type': 'application/json',
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
}

describe('recommendation-preferences BFF route', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    cookieStore.delete(AUTH_COOKIE_ACCESS)
    cookieStore.delete(AUTH_COOKIE_REFRESH)
    cookieStore.setCalls.length = 0
  })

  it('returns 401 when unauthenticated', async () => {
    const res = await GET(
      new Request('http://localhost:3000/api/account/recommendation-preferences'),
    )
    expect(res.status).toBe(401)
  })

  it('forwards GET to the backend preferences endpoint when authenticated', async () => {
    cookieStore.set(AUTH_COOKIE_ACCESS, makeJwt(Math.floor(Date.now() / 1000) + 3600))
    const fetchMock = vi.fn(async (url: RequestInfo | URL) => {
      expect(String(url)).toContain('/api/users/me/recommendation-preferences')
      return Response.json({ isPersonalized: false, optedInAt: null })
    })
    vi.stubGlobal('fetch', fetchMock)

    const res = await GET(
      new Request('http://localhost:3000/api/account/recommendation-preferences'),
    )
    expect(res.status).toBe(200)
    expect(await res.json()).toEqual({ isPersonalized: false, optedInAt: null })
  })

  it('rejects malformed JSON with 400 without forwarding', async () => {
    cookieStore.set(AUTH_COOKIE_ACCESS, makeJwt(Math.floor(Date.now() / 1000) + 3600))
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)

    const res = await PATCH(
      new Request('http://localhost:3000/api/account/recommendation-preferences', {
        method: 'PATCH',
        headers: { Origin: 'http://localhost:3000', 'Content-Type': 'application/json' },
        body: '{invalid',
      }),
    )
    expect(res.status).toBe(400)
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('rejects non-boolean payloads with 400 without forwarding', async () => {
    cookieStore.set(AUTH_COOKIE_ACCESS, makeJwt(Math.floor(Date.now() / 1000) + 3600))
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)

    const res = await PATCH(
      authedRequest('http://localhost:3000/api/account/recommendation-preferences', 'PATCH', {
        isPersonalized: 'yes',
      }),
    )
    expect(res.status).toBe(400)
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('forwards a valid opt-in PATCH and returns the saved state', async () => {
    cookieStore.set(AUTH_COOKIE_ACCESS, makeJwt(Math.floor(Date.now() / 1000) + 3600))
    const fetchMock = vi.fn(async (url: RequestInfo | URL, init?: RequestInit) => {
      expect(String(url)).toContain('/api/users/me/recommendation-preferences')
      expect(init?.method).toBe('PATCH')
      expect(JSON.parse(String(init?.body))).toEqual({ isPersonalized: true })
      return Response.json({ isPersonalized: true, optedInAt: '2026-09-14T00:00:00.000Z' })
    })
    vi.stubGlobal('fetch', fetchMock)

    const res = await PATCH(
      authedRequest('http://localhost:3000/api/account/recommendation-preferences', 'PATCH', {
        isPersonalized: true,
      }),
    )
    expect(res.status).toBe(200)
    expect(await res.json()).toEqual({
      isPersonalized: true,
      optedInAt: '2026-09-14T00:00:00.000Z',
    })
  })
})
