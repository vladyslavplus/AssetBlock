import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { GET } from '@/app/api/seller/assets/[id]/versions/[versionId]/analysis/reports/[reportId]/findings/route'
import { AUTH_COOKIE_ACCESS } from '@/lib/auth/constants'
import { createMemoryCookieStore, makeJwt } from '@/test/cookie-store'

let cookieStore = createMemoryCookieStore()

vi.mock('next/headers', () => ({ cookies: async () => cookieStore }))
vi.mock('server-only', () => ({}))

describe('seller analysis findings BFF route', () => {
  const assetId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
  const versionId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'
  const reportId = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee'
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

  it('validates every route identity and page before forwarding safe page requests', async () => {
    const fetchMock = vi.fn(async () => new Response('{"items":[]}', { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)
    const response = await GET(
      new Request(
        `http://localhost:3000/api/seller/assets/${assetId}/versions/${versionId}/analysis/reports/${reportId}/findings?page=2`,
      ),
      { params: Promise.resolve({ id: assetId, versionId, reportId }) },
    )

    expect(response.status).toBe(200)
    const [url] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(url).toBe(
      `${backendBaseUrl}/api/users/me/asset-versions/${versionId}/analysis/reports/${reportId}/findings?page=2`,
    )
  })

  it('rejects invalid asset identity and oversized page before backend access', async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    const request = new Request(
      `http://localhost:3000/api/seller/assets/nope/versions/${versionId}/analysis/reports/${reportId}/findings?page=100001`,
    )

    const response = await GET(request, {
      params: Promise.resolve({ id: 'nope', versionId, reportId }),
    })

    expect(response.status).toBe(400)
    expect(fetchMock).not.toHaveBeenCalled()
  })
})
