import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { POST } from './route'
import {
  sellerReviewBackendEndpoints,
  sellerReviewBackendPath,
} from '@/lib/seller/seller-review-routes'
import { AUTH_COOKIE_ACCESS } from '@/lib/auth/constants'
import { createMemoryCookieStore, makeJwt } from '@/test/cookie-store'

let store = createMemoryCookieStore()
vi.mock('next/headers', () => ({ cookies: async () => store }))
vi.mock('server-only', () => ({}))
const id = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const versionId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
const context = { params: Promise.resolve({ id, versionId }) }
const body = { operationId: id, workspaceId: id, reportId: versionId, expectedWorkspaceRevision: 2 }
function request(value: unknown, origin = 'http://localhost:3000') {
  return new Request('http://localhost:3000/coverage-reviews', {
    method: 'POST',
    headers: { Origin: origin, 'Content-Type': 'application/json' },
    body: JSON.stringify(value),
  })
}
describe('seller coverage request BFF', () => {
  beforeEach(() => {
    store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: makeJwt(Math.floor(Date.now() / 1000) + 3600),
    })
    vi.stubEnv('ASSETBLOCK_API_BASE_URL', 'https://api.example.test')
    vi.stubEnv('NEXT_PUBLIC_API_BASE_URL', 'https://api.example.test')
  })
  afterEach(() => {
    vi.unstubAllGlobals()
    vi.unstubAllEnvs()
  })
  it('uses the exact shared backend path and POST method exercised by real HTTP acceptance', async () => {
    const fetchMock = vi.fn(async () => new Response('{}'))
    vi.stubGlobal('fetch', fetchMock)
    const response = await POST(request(body), context)
    expect(response.status).toBe(200)
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(url).toBe(
      `https://api.example.test/api/assets/${id}/versions/${versionId}/coverage-reviews`,
    )
    expect(url).toBe(
      'https://api.example.test' + sellerReviewBackendPath('coverage', id, versionId),
    )
    expect(init.method).toBe('POST')
    expect(init.method).toBe(sellerReviewBackendEndpoints.coverage.writeMethod)
    expect(JSON.parse(init.body as string)).toEqual(body)
    expect(response.headers.get('Cache-Control')).toBe('private, no-store')
  })
  it('returns field errors without forwarding invalid revisions', async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    const response = await POST(request({ ...body, expectedWorkspaceRevision: 0 }), context)
    expect(response.status).toBe(400)
    expect(await response.text()).toContain('expectedWorkspaceRevision')
    expect(fetchMock).not.toHaveBeenCalled()
  })
  it('checks origin before parsing malformed JSON', async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    const response = await POST(
      new Request('http://localhost:3000/coverage-reviews', {
        method: 'POST',
        headers: { Origin: 'https://foreign.example' },
        body: '{',
      }),
      context,
    )
    expect(response.status).toBe(403)
    expect(fetchMock).not.toHaveBeenCalled()
  })
})
