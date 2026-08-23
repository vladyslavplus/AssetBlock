import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { AUTH_COOKIE_ACCESS, AUTH_COOKIE_REFRESH } from '@/lib/auth/constants'
import { fetchBackend } from '@/lib/server/fetch-backend'
import { createMemoryCookieStore, makeJwt } from '@/test/cookie-store'

const API = 'http://api.test'

function jsonResponse(body: unknown, status = 200, extraHeaders?: HeadersInit): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...extraHeaders },
  })
}

function rotatedTokens() {
  return {
    accessToken: makeJwt(Math.floor(Date.now() / 1000) + 3600),
    refreshToken: 'rotated-refresh',
    accessExpiresAt: new Date(Date.now() + 3600_000).toISOString(),
    refreshExpiresAt: new Date(Date.now() + 86400_000).toISOString(),
  }
}

describe('fetchBackend session refresh', () => {
  beforeEach(() => {
    vi.stubEnv('ASSETBLOCK_API_BASE_URL', API)
    vi.stubEnv('NEXT_PUBLIC_API_BASE_URL', API)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    vi.unstubAllEnvs()
  })

  it('does not refresh when the access JWT is still valid', async () => {
    const access = makeJwt(Math.floor(Date.now() / 1000) + 3600)
    const store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: access,
      [AUTH_COOKIE_REFRESH]: 'refresh-token',
    })
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => {
      const url = String(_input)
      if (url.includes('/api/auth/refresh')) {
        throw new Error('refresh must not run')
      }
      return jsonResponse({ ok: true })
    })
    vi.stubGlobal('fetch', fetchMock)

    const res = await fetchBackend(store, '/api/seller/listings', { method: 'GET' }, 'required')
    expect(res.status).toBe(200)
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const firstCall = fetchMock.mock.calls[0]
    expect(firstCall).toBeDefined()
    const init = (firstCall?.[1] ?? {}) as RequestInit
    expect(new Headers(init.headers).get('Authorization')).toBe(`Bearer ${access}`)
  })

  it('refreshes once and retries the original request after a backend 401', async () => {
    const access = makeJwt(Math.floor(Date.now() / 1000) + 3600)
    const rotated = rotatedTokens()
    const store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: access,
      [AUTH_COOKIE_REFRESH]: 'refresh-token',
    })

    let resourceCalls = 0
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url === `${API}/api/auth/refresh`) {
        return jsonResponse(rotated)
      }
      resourceCalls += 1
      if (resourceCalls === 1) {
        return jsonResponse({ title: 'Unauthorized' }, 401)
      }
      expect(new Headers(init?.headers).get('Authorization')).toBe(`Bearer ${rotated.accessToken}`)
      return jsonResponse({ items: [] })
    })
    vi.stubGlobal('fetch', fetchMock)

    const res = await fetchBackend(store, '/api/seller/listings', { method: 'GET' }, 'required')
    expect(res.status).toBe(200)
    expect(await res.json()).toEqual({ items: [] })
    expect(store.snapshot()[AUTH_COOKIE_ACCESS]).toBe(rotated.accessToken)
    expect(store.snapshot()[AUTH_COOKIE_REFRESH]).toBe('rotated-refresh')
    expect(resourceCalls).toBe(2)
    expect(fetchMock).toHaveBeenCalled()
  })

  it('returns 401 and does not loop when refresh fails after a backend 401', async () => {
    const access = makeJwt(Math.floor(Date.now() / 1000) + 3600)
    const store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: access,
      [AUTH_COOKIE_REFRESH]: 'refresh-token',
    })
    let resourceCalls = 0
    let refreshCalls = 0
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url === `${API}/api/auth/refresh`) {
        refreshCalls += 1
        return jsonResponse({ title: 'Unauthorized' }, 401)
      }
      resourceCalls += 1
      return jsonResponse({ title: 'Unauthorized' }, 401)
    })

    const res = await fetchBackend(store, '/api/seller/listings', { method: 'GET' }, 'required')
    expect(res.status).toBe(401)
    expect(resourceCalls).toBe(1)
    expect(refreshCalls).toBe(1)
  })

  it('does not put tokens in the 401 body when refresh is missing', async () => {
    const store = createMemoryCookieStore()
    vi.stubGlobal('fetch', async () => {
      throw new Error('backend must not be called without access')
    })
    const res = await fetchBackend(store, '/api/seller/listings', { method: 'GET' }, 'required')
    expect(res.status).toBe(401)
    expect(await res.text()).toBe('')
  })
})
