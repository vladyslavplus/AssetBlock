import { afterEach, describe, expect, it, vi } from 'vitest'
import { GET } from './route'
import { AUTH_COOKIE_ACCESS } from '@/lib/auth/constants'
import { createMemoryCookieStore, makeJwt } from '@/test/cookie-store'

const cookieStore = createMemoryCookieStore()

vi.mock('next/headers', () => ({
  cookies: async () => cookieStore,
}))

vi.mock('server-only', () => ({}))

describe('GET /api/assets/[id]/download', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    cookieStore.delete(AUTH_COOKIE_ACCESS)
  })

  it('returns 400 ProblemDetails when id is not a valid UUID', async () => {
    const request = new Request('http://localhost:3000/api/assets/bad-id/download')
    const res = await GET(request, { params: Promise.resolve({ id: 'bad-id' }) })

    expect(res.status).toBe(400)
    const body = await res.json()
    expect(body.code).toBe('ERR_VALIDATION_FAILED')
    expect(body.errors?.id).toBeDefined()
  })

  it('returns 400 ProblemDetails when versionId query param is not a valid UUID', async () => {
    const validAssetId = '123e4567-e89b-12d3-a456-426614174000'
    const request = new Request(`http://localhost:3000/api/assets/${validAssetId}/download?versionId=not-a-uuid`)
    const res = await GET(request, { params: Promise.resolve({ id: validAssetId }) })

    expect(res.status).toBe(400)
    const body = await res.json()
    expect(body.code).toBe('ERR_VALIDATION_FAILED')
    expect(body.errors?.versionId).toBeDefined()
  })

  it('forwards download request for asset and returns Cache-Control: no-store', async () => {
    const validAssetId = '123e4567-e89b-12d3-a456-426614174000'
    const token = makeJwt(Math.floor(Date.now() / 1000) + 3600)
    cookieStore.set(AUTH_COOKIE_ACCESS, token)

    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      expect(String(input)).toContain(`/api/assets/${validAssetId}/download`)
      return new Response('file-content', {
        status: 200,
        headers: {
          'Content-Type': 'application/zip',
          'Content-Disposition': 'attachment; filename="test.zip"',
        },
      })
    })
    vi.stubGlobal('fetch', fetchMock)

    const request = new Request(`http://localhost:3000/api/assets/${validAssetId}/download`)
    const res = await GET(request, { params: Promise.resolve({ id: validAssetId }) })

    expect(res.status).toBe(200)
    expect(res.headers.get('Content-Type')).toBe('application/zip')
    expect(res.headers.get('Content-Disposition')).toBe('attachment; filename="test.zip"')
    expect(res.headers.get('Cache-Control')).toBe('no-store')
  })

  it('forwards version download when versionId is specified', async () => {
    const validAssetId = '123e4567-e89b-12d3-a456-426614174000'
    const validVersionId = '987fcdeb-51a2-43d7-9876-543210987654'
    const token = makeJwt(Math.floor(Date.now() / 1000) + 3600)
    cookieStore.set(AUTH_COOKIE_ACCESS, token)

    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      expect(String(input)).toContain(`/api/assets/${validAssetId}/versions/${validVersionId}/download`)
      return new Response('version-file', {
        status: 200,
        headers: {
          'Content-Type': 'application/zip',
          'Content-Disposition': 'attachment; filename="v2.zip"',
        },
      })
    })
    vi.stubGlobal('fetch', fetchMock)

    const request = new Request(`http://localhost:3000/api/assets/${validAssetId}/download?versionId=${validVersionId}`)
    const res = await GET(request, { params: Promise.resolve({ id: validAssetId }) })

    expect(res.status).toBe(200)
    expect(res.headers.get('Cache-Control')).toBe('no-store')
  })
})
