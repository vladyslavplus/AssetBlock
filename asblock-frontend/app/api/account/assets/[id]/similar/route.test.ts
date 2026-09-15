import { afterEach, describe, expect, it, vi } from 'vitest'
import { GET } from './route'
import { AUTH_COOKIE_ACCESS, AUTH_COOKIE_REFRESH } from '@/lib/auth/constants'
import { createMemoryCookieStore, makeJwt } from '@/test/cookie-store'

const cookieStore = createMemoryCookieStore()

vi.mock('next/headers', () => ({
  cookies: async () => cookieStore,
}))

vi.mock('server-only', () => ({}))

const assetId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'

function routeParams() {
  return { params: Promise.resolve({ id: assetId }) }
}

describe('personal similar-assets BFF route', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    cookieStore.delete(AUTH_COOKIE_ACCESS)
    cookieStore.delete(AUTH_COOKIE_REFRESH)
    cookieStore.setCalls.length = 0
  })

  it('returns 401 when unauthenticated', async () => {
    const res = await GET(
      new Request(`http://localhost:3000/api/account/assets/${assetId}/similar`),
      routeParams(),
    )
    expect(res.status).toBe(401)
  })

  it('rejects a non-uuid asset id with 400 without forwarding', async () => {
    cookieStore.set(AUTH_COOKIE_ACCESS, makeJwt(Math.floor(Date.now() / 1000) + 3600))
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)

    const res = await GET(
      new Request('http://localhost:3000/api/account/assets/not-a-uuid/similar'),
      { params: Promise.resolve({ id: 'not-a-uuid' }) },
    )
    expect(res.status).toBe(400)
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('rejects an unknown mode with 400 without forwarding', async () => {
    cookieStore.set(AUTH_COOKIE_ACCESS, makeJwt(Math.floor(Date.now() / 1000) + 3600))
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)

    const res = await GET(
      new Request(`http://localhost:3000/api/account/assets/${assetId}/similar?mode=trending`),
      routeParams(),
    )
    expect(res.status).toBe(400)
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('forwards limit and mode to the authenticated backend route', async () => {
    cookieStore.set(AUTH_COOKIE_ACCESS, makeJwt(Math.floor(Date.now() / 1000) + 3600))
    const fetchMock = vi.fn(async (url: RequestInfo | URL) => {
      expect(String(url)).toContain(
        `/api/users/me/assets/${assetId}/similar?limit=6&mode=popularity`,
      )
      return Response.json({ items: [], exposure: null })
    })
    vi.stubGlobal('fetch', fetchMock)

    const res = await GET(
      new Request(
        `http://localhost:3000/api/account/assets/${assetId}/similar?limit=6&mode=popularity`,
      ),
      routeParams(),
    )
    expect(res.status).toBe(200)
    expect(await res.json()).toEqual({ items: [], exposure: null })
  })
})
