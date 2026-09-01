import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { GET, POST } from '@/app/api/seller/asset-versions/[id]/listing-copilot/route'
import { AUTH_COOKIE_ACCESS } from '@/lib/auth/constants'
import { createMemoryCookieStore, makeJwt } from '@/test/cookie-store'

let cookieStore = createMemoryCookieStore()

vi.mock('next/headers', () => ({
  cookies: async () => cookieStore,
}))

vi.mock('server-only', () => ({}))

describe('listing-copilot BFF route', () => {
  const validVersionId = '123e4567-e89b-12d3-a456-426614174000'
  const backendBaseUrl = 'https://api.example.test'

  beforeEach(() => {
    cookieStore = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: makeJwt(Math.floor(Date.now() / 1000) + 3600),
    })
    vi.stubEnv('ASSETBLOCK_API_BASE_URL', backendBaseUrl)
    vi.stubEnv('NEXT_PUBLIC_API_BASE_URL', backendBaseUrl)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    vi.unstubAllEnvs()
  })

  it('forwards authenticated GET with no-store response headers', async () => {
    const fetchMock = vi.fn(
      async () =>
        new Response('{"suggestion":"ready"}', {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
    )
    vi.stubGlobal('fetch', fetchMock)

    const request = new Request(
      `http://localhost:3000/api/seller/asset-versions/${validVersionId}/listing-copilot`,
    )
    const res = await GET(request, { params: Promise.resolve({ id: validVersionId }) })

    expect(res.status).toBe(200)
    expect(res.headers.get('Cache-Control')).toBe('private, no-store')
    expect(res.headers.get('Vary')).toBe('Cookie')
    expect(fetchMock).toHaveBeenCalledOnce()
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(url).toBe(
      `${backendBaseUrl}/api/users/me/asset-versions/${validVersionId}/listing-copilot`,
    )
    expect(init.cache).toBe('no-store')
    expect(new Headers(init.headers).get('Authorization')).toMatch(/^Bearer /)
  })

  it('returns 401 without calling the backend when the session is missing', async () => {
    cookieStore = createMemoryCookieStore()
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)

    const res = await GET(
      new Request(
        `http://localhost:3000/api/seller/asset-versions/${validVersionId}/listing-copilot`,
      ),
      { params: Promise.resolve({ id: validVersionId }) },
    )

    expect(res.status).toBe(401)
    expect(res.headers.get('Cache-Control')).toBe('private, no-store')
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('rejects cross-origin POST before auth or backend access', async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)

    const res = await POST(
      new Request(
        `http://localhost:3000/api/seller/asset-versions/${validVersionId}/listing-copilot`,
        { method: 'POST', headers: { Origin: 'https://evil.test' } },
      ),
      { params: Promise.resolve({ id: validVersionId }) },
    )

    expect(res.status).toBe(403)
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('forwards same-origin POST and preserves backend status safely', async () => {
    const fetchMock = vi.fn(
      async () =>
        new Response('{"code":"ERR_LISTING_COPILOT_FAILED","detail":"Generation failed."}', {
          status: 409,
          headers: { 'Content-Type': 'application/problem+json' },
        }),
    )
    vi.stubGlobal('fetch', fetchMock)
    const request = new Request(
      `http://localhost:3000/api/seller/asset-versions/${validVersionId}/listing-copilot`,
      { method: 'POST', headers: { Origin: 'http://localhost:3000' } },
    )

    const res = await POST(request, { params: Promise.resolve({ id: validVersionId }) })

    expect(res.status).toBe(409)
    expect(res.headers.get('Cache-Control')).toBe('private, no-store')
    expect(fetchMock).toHaveBeenCalledOnce()
    const [, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(init.method).toBe('POST')
    expect(init.signal).toBeInstanceOf(AbortSignal)
  })

  it('rejects an invalid UUID before auth or backend access', async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)

    const res = await GET(
      new Request('http://localhost:3000/api/seller/asset-versions/not-a-uuid/listing-copilot'),
      { params: Promise.resolve({ id: 'not-a-uuid' }) },
    )

    expect(res.status).toBe(400)
    expect((await res.json()).code).toBe('ERR_VALIDATION_FAILED')
    expect(fetchMock).not.toHaveBeenCalled()
  })
})
