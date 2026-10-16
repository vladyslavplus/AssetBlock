import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { POST } from '@/app/api/seller/assets/[id]/versions/[versionId]/analysis-retries/route'
import { AUTH_COOKIE_ACCESS } from '@/lib/auth/constants'
import { createMemoryCookieStore, makeJwt } from '@/test/cookie-store'

let cookieStore = createMemoryCookieStore()

vi.mock('next/headers', () => ({ cookies: async () => cookieStore }))
vi.mock('server-only', () => ({}))

describe('seller analysis retry BFF route', () => {
  const assetId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
  const versionId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'
  const backendBaseUrl = 'https://api.example.test'
  const body = {
    operationId: 'ffffffff-ffff-4fff-8fff-ffffffffffff',
    expectedRunId: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
    expectedWorkspaceRevision: 3,
    expectedPolicyDigest: 'a'.repeat(64),
    expectedInputDigest: 'b'.repeat(64),
  }

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

  it('forwards same-origin retry and preserves accepted recheck status', async () => {
    const fetchMock = vi.fn(
      async () => new Response(JSON.stringify({ status: 'RECHECK_QUEUED' }), { status: 202 }),
    )
    vi.stubGlobal('fetch', fetchMock)
    const request = new Request(
      `http://localhost:3000/api/seller/assets/${assetId}/versions/${versionId}/analysis-retries`,
      {
        method: 'POST',
        headers: { Origin: 'http://localhost:3000', 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      },
    )

    const response = await POST(request, { params: Promise.resolve({ id: assetId, versionId }) })

    expect(response.status).toBe(202)
    expect(fetchMock).toHaveBeenCalledOnce()
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(url).toBe(
      `${backendBaseUrl}/api/users/me/assets/${assetId}/versions/${versionId}/analysis-retries`,
    )
    expect(init.body).toBe(JSON.stringify(body))
    expect(new Headers(init.headers).get('Authorization')).toMatch(/^Bearer /)
  })

  it('rejects malformed expectations before contacting backend', async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    const request = new Request(
      `http://localhost:3000/api/seller/assets/${assetId}/versions/${versionId}/analysis-retries`,
      {
        method: 'POST',
        headers: { Origin: 'http://localhost:3000', 'Content-Type': 'application/json' },
        body: JSON.stringify({ ...body, expectedInputDigest: 'not-a-digest' }),
      },
    )

    const response = await POST(request, { params: Promise.resolve({ id: assetId, versionId }) })

    expect(response.status).toBe(400)
    expect(fetchMock).not.toHaveBeenCalled()
  })
})
