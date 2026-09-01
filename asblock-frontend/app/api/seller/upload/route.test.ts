import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { POST as publishVersion } from '@/app/api/seller/assets/[id]/versions/route'
import { POST as uploadAsset } from '@/app/api/seller/upload/route'
import { AUTH_COOKIE_ACCESS, AUTH_COOKIE_REFRESH } from '@/lib/auth/constants'
import { createMemoryCookieStore, makeJwt } from '@/test/cookie-store'

const backendBaseUrl = 'http://api.test'
const boundary = 'assetblock-route-test-boundary'
const cookieStore = createMemoryCookieStore()

vi.mock('next/headers', () => ({
  cookies: async () => cookieStore,
}))

vi.mock('server-only', () => ({}))

function multipartRequest(
  path: string,
  parts: ReadonlyArray<{ name: string; value: string; filename?: string }>,
): Request {
  const body = `${parts
    .map(({ name, value, filename }) => {
      const disposition = filename
        ? `form-data; name="${name}"; filename="${filename}"`
        : `form-data; name="${name}"`
      return `--${boundary}\r\nContent-Disposition: ${disposition}\r\n\r\n${value}\r\n`
    })
    .join('')}--${boundary}--\r\n`

  return new Request(`http://localhost:3000${path}`, {
    method: 'POST',
    headers: {
      Origin: 'http://localhost:3000',
      'Content-Type': `multipart/form-data; boundary=${boundary}`,
      'Content-Length': String(new TextEncoder().encode(body).byteLength),
    },
    body,
  })
}

async function forwardedBody(init: RequestInit | undefined): Promise<string> {
  if (!init?.body) return ''
  return new Response(init.body).text()
}

describe('seller multipart streaming routes', () => {
  beforeEach(() => {
    vi.stubEnv('ASSETBLOCK_API_BASE_URL', backendBaseUrl)
    vi.stubEnv('NEXT_PUBLIC_API_BASE_URL', backendBaseUrl)
    cookieStore.set(AUTH_COOKIE_ACCESS, makeJwt(Math.floor(Date.now() / 1000) + 3600))
    cookieStore.set(AUTH_COOKIE_REFRESH, 'refresh-token')
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    vi.unstubAllEnvs()
    cookieStore.delete(AUTH_COOKIE_ACCESS)
    cookieStore.delete(AUTH_COOKIE_REFRESH)
  })

  it('normalizes whitelisted asset metadata and streams the file to the backend', async () => {
    let backendBody = ''
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      backendBody = await forwardedBody(init)
      return Response.json({ id: '123e4567-e89b-12d3-a456-426614174000' })
    })
    vi.stubGlobal('fetch', fetchMock)
    const request = multipartRequest('/api/seller/upload', [
      { name: 'title', value: '  Український asset  ' },
      { name: 'description', value: 'Description' },
      { name: 'price', value: '12.50' },
      { name: 'categoryId', value: '123e4567-e89b-12d3-a456-426614174000' },
      { name: 'licenseCode', value: 'PERSONAL' },
      { name: 'unknown', value: 'must-not-forward' },
      { name: 'file', filename: 'asset.zip', value: 'archive-bytes' },
    ])

    const response = await uploadAsset(request)

    expect(response.status).toBe(200)
    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(backendBody).toContain('name="title"\r\n\r\nУкраїнський asset')
    expect(backendBody).toContain('name="price"\r\n\r\n12.5')
    expect(backendBody).toContain('filename="asset.zip"\r\n\r\narchive-bytes')
    expect(backendBody).not.toContain('unknown')
  })

  it('normalizes version metadata and streams the archive to the dynamic backend route', async () => {
    let calledUrl = ''
    let backendBody = ''
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      calledUrl = String(input)
      backendBody = await forwardedBody(init)
      return Response.json({ version: 2 })
    })
    vi.stubGlobal('fetch', fetchMock)
    const assetId = '123e4567-e89b-12d3-a456-426614174000'
    const request = multipartRequest(`/api/seller/assets/${assetId}/versions`, [
      { name: 'licenseCode', value: 'COMMERCIAL' },
      { name: 'releaseNotes', value: '  Новий реліз  ' },
      { name: 'file', filename: 'asset-v2.tar.gz', value: 'new-archive-bytes' },
    ])

    const response = await publishVersion(request, {
      params: Promise.resolve({ id: assetId }),
    })

    expect(response.status).toBe(200)
    expect(calledUrl).toBe(`${backendBaseUrl}/api/assets/${assetId}/versions`)
    expect(backendBody).toContain('name="releaseNotes"\r\n\r\nНовий реліз')
    expect(backendBody).toContain('filename="asset-v2.tar.gz"\r\n\r\nnew-archive-bytes')
  })
})
