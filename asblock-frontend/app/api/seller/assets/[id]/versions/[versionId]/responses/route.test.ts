import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { GET, POST } from './route'
import { PUT } from '../evidence/route'
import {
  sellerReviewBackendEndpoints,
  sellerReviewBackendPath,
} from '@/lib/seller/seller-review-routes'
import { AUTH_COOKIE_ACCESS } from '@/lib/auth/constants'
import { createMemoryCookieStore, makeJwt } from '@/test/cookie-store'

let cookieStore = createMemoryCookieStore()
vi.mock('next/headers', () => ({ cookies: async () => cookieStore }))
vi.mock('server-only', () => ({}))
const id = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const versionId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
const context = { params: Promise.resolve({ id, versionId }) }
const evidence = {
  workspaceId: id,
  reportId: id,
  operationId: id,
  expectedWorkspaceRevision: 2,
  expectedEvidenceRevision: 1,
  text: ' private explanation ',
}
const body = { ...evidence, findingId: versionId, responseType: 'PRIOR_WORK' }
function request(payload: unknown, origin = 'http://localhost:3000') {
  return new Request(
    `http://localhost:3000/api/seller/assets/${id}/versions/${versionId}/responses`,
    {
      method: 'POST',
      headers: { Origin: origin, 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    },
  )
}
describe('seller review evidence BFF', () => {
  beforeEach(() => {
    cookieStore = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: makeJwt(Math.floor(Date.now() / 1000) + 3600),
    })
    vi.stubEnv('ASSETBLOCK_API_BASE_URL', 'https://api.example.test')
    vi.stubEnv('NEXT_PUBLIC_API_BASE_URL', 'https://api.example.test')
  })
  afterEach(() => {
    vi.unstubAllGlobals()
    vi.unstubAllEnvs()
  })
  it.each([
    { ...body, responseType: 'INVALID' },
    { ...body, text: 'x'.repeat(2001) },
    { ...body, text: ' ' },
    { ...body, expectedEvidenceRevision: -1 },
    { ...body, tokenScore: 1 },
  ])('rejects invalid or private producer fields', async (payload) => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    expect((await POST(request(payload), context)).status).toBe(400)
    expect(fetchMock).not.toHaveBeenCalled()
  })
  it('rejects cross-origin writes', async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    expect((await POST(request(body, 'https://foreign.example'), context)).status).toBe(403)
    expect(fetchMock).not.toHaveBeenCalled()
  })
  it('forwards trimmed response and preserves evidence conflict and private cache headers', async () => {
    const fetchMock = vi.fn(
      async () =>
        new Response(
          JSON.stringify({
            code: 'ERR_SELLER_EVIDENCE_STALE',
            message: 'Review evidence changed.',
          }),
          { status: 409 },
        ),
    )
    vi.stubGlobal('fetch', fetchMock)
    const response = await POST(request(body), context)
    expect(response.status).toBe(409)
    expect(response.headers.get('Cache-Control')).toBe('private, no-store')
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(url).toBe(`https://api.example.test/api/assets/${id}/versions/${versionId}/responses`)
    expect(init.method).toBe('POST')
    expect(init.method).toBe(sellerReviewBackendEndpoints.responses.writeMethod)
    expect(url).toBe(
      'https://api.example.test' + sellerReviewBackendPath('responses', id, versionId),
    )
    expect(JSON.parse(init.body as string).text).toBe('private explanation')
  })
  it('allows empty No evidence and evidence-only notes', async () => {
    const fetchMock = vi.fn(async () => new Response('{}', { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)
    expect(
      (await POST(request({ ...body, responseType: 'NO_EVIDENCE', text: '' }), context)).status,
    ).toBe(200)
    expect((await PUT(request(evidence), context)).status).toBe(200)
    expect(fetchMock.mock.calls).toHaveLength(2)
  })
  it('rejects invalid history page before forwarding', async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    expect((await GET(new Request('http://localhost:3000/responses?page=0'), context)).status).toBe(
      400,
    )
    expect(fetchMock).not.toHaveBeenCalled()
  })
  it('forwards the exact response history path, page and GET method', async () => {
    const fetchMock = vi.fn(async () => new Response('{}'))
    vi.stubGlobal('fetch', fetchMock)
    await GET(new Request('http://localhost:3000/responses?page=3'), context)
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(url).toBe(
      `https://api.example.test/api/assets/${id}/versions/${versionId}/responses?page=3`,
    )
    expect(init.method).toBe('GET')
    expect(init.method).toBe(sellerReviewBackendEndpoints.responses.readMethod)
    expect(url).toBe(
      'https://api.example.test' + sellerReviewBackendPath('responses', id, versionId) + '?page=3',
    )
  })
  it('forwards the exact evidence path and PUT method', async () => {
    const fetchMock = vi.fn(async () => new Response('{}'))
    vi.stubGlobal('fetch', fetchMock)
    await PUT(request(evidence), context)
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(url).toBe(`https://api.example.test/api/assets/${id}/versions/${versionId}/evidence`)
    expect(init.method).toBe('PUT')
    expect(init.method).toBe(sellerReviewBackendEndpoints.evidence.writeMethod)
    expect(url).toBe(
      'https://api.example.test' + sellerReviewBackendPath('evidence', id, versionId),
    )
  })

  it.each([POST, PUT])('rejects cross-origin invalid JSON before body parsing', async (handler) => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    const request = new Request('http://localhost:3000/responses', {
      method: 'POST',
      headers: { Origin: 'https://foreign.example' },
      body: '{',
    })
    expect((await handler(request, context)).status).toBe(403)
    expect(fetchMock).not.toHaveBeenCalled()
  })
  it.each([POST, PUT])('preserves field validation errors', async (handler) => {
    const response = await handler(request({ ...evidence, text: 'x'.repeat(2001) }), context)
    expect(response.status).toBe(400)
    expect(await response.text()).toContain('text')
  })
})
