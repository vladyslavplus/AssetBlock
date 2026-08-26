import { afterEach, describe, expect, it, vi } from 'vitest'

import { GET, POST } from '@/app/api/seller/asset-versions/[id]/listing-copilot/route'
import { createMemoryCookieStore } from '@/test/cookie-store'

const cookieStore = createMemoryCookieStore()
const fetchBackendAuthorized = vi.hoisted(() => vi.fn())

vi.mock('next/headers', () => ({
  cookies: async () => cookieStore,
}))

vi.mock('server-only', () => ({}))

vi.mock('@/lib/server/backend-authorized', () => ({
  fetchBackendAuthorized: (...args: unknown[]) => fetchBackendAuthorized(...args),
}))

describe('listing-copilot BFF route', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    fetchBackendAuthorized.mockReset()
  })

  it('forwards GET to the owner listing-copilot endpoint', async () => {
    fetchBackendAuthorized.mockResolvedValue(new Response('{}', { status: 200 }))
    const res = await GET(
      new Request('http://localhost:3000/api/seller/asset-versions/abc/listing-copilot'),
      {
        params: Promise.resolve({ id: 'abc' }),
      },
    )
    expect(res.status).toBe(200)
    expect(fetchBackendAuthorized).toHaveBeenCalledWith(
      cookieStore,
      '/api/users/me/asset-versions/abc/listing-copilot',
      { method: 'GET' },
    )
  })

  it('rejects cross-origin POST', async () => {
    const res = await POST(
      new Request('http://localhost:3000/api/seller/asset-versions/abc/listing-copilot', {
        method: 'POST',
        headers: { Origin: 'https://evil.test' },
      }),
      { params: Promise.resolve({ id: 'abc' }) },
    )
    expect(res.status).toBe(403)
    expect(fetchBackendAuthorized).not.toHaveBeenCalled()
  })

  it('forwards same-origin POST without a browser token', async () => {
    fetchBackendAuthorized.mockResolvedValue(new Response('{"jobId":"1"}', { status: 202 }))
    const res = await POST(
      new Request('http://localhost:3000/api/seller/asset-versions/abc/listing-copilot', {
        method: 'POST',
        headers: { Origin: 'http://localhost:3000' },
      }),
      { params: Promise.resolve({ id: 'abc' }) },
    )
    expect(res.status).toBe(202)
    const [, path, init] = fetchBackendAuthorized.mock.calls[0] as [
      unknown,
      string,
      { method: string },
    ]
    expect(path).toBe('/api/users/me/asset-versions/abc/listing-copilot')
    expect(init.method).toBe('POST')
    expect(JSON.stringify(init)).not.toContain('Bearer')
  })
})
