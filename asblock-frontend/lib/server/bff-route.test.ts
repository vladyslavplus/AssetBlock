import { beforeEach, describe, expect, it, vi } from 'vitest'

import { proxyAuthenticatedBff } from '@/lib/server/bff-route'

const mocks = vi.hoisted(() => ({ cookies: vi.fn(), authorized: vi.fn() }))
vi.mock('next/headers', () => ({ cookies: mocks.cookies }))
vi.mock('@/lib/server/backend-authorized', () => ({
  fetchBackendAuthorized: mocks.authorized,
}))

beforeEach(() => {
  mocks.cookies.mockReset()
  mocks.authorized.mockReset()
  mocks.cookies.mockResolvedValue({ get: vi.fn(), set: vi.fn(), delete: vi.fn() })
  mocks.authorized.mockResolvedValue(Response.json({ ok: true }))
})

describe('proxyAuthenticatedBff', () => {
  it('rejects cross-site mutation before cookie or backend access', async () => {
    const response = await proxyAuthenticatedBff(
      new Request('http://localhost/api/test', {
        method: 'POST',
        headers: { Origin: 'https://evil.example', 'Sec-Fetch-Site': 'cross-site' },
      }),
      { path: '/api/test', init: { method: 'POST' }, enforceSameOrigin: true },
    )
    expect(response.status).toBe(403)
    expect(mocks.cookies).not.toHaveBeenCalled()
    expect(mocks.authorized).not.toHaveBeenCalled()
  })

  it('rejects absolute and non-API backend paths', async () => {
    for (const path of ['https://evil.example/api', '//evil.example/api', '/health']) {
      const response = await proxyAuthenticatedBff(new Request('http://localhost/api/test'), {
        path,
      })
      expect(response.status).toBe(500)
    }
    expect(mocks.authorized).not.toHaveBeenCalled()
  })

  it('passes caller AbortSignal and applies private response headers', async () => {
    const controller = new AbortController()
    const request = new Request('http://localhost/api/test', { signal: controller.signal })
    const response = await proxyAuthenticatedBff(request, {
      path: '/api/users/me',
      init: { method: 'GET' },
    })
    expect(mocks.authorized).toHaveBeenCalledWith(
      expect.anything(),
      '/api/users/me',
      expect.objectContaining({ method: 'GET', signal: request.signal }),
    )
    expect(response.headers.get('Cache-Control')).toBe('private, no-store')
    expect(response.headers.get('Vary')).toBe('Cookie')
  })

  it('reuses a caller-provided cookie store', async () => {
    const cookieStore = { get: vi.fn(), set: vi.fn(), delete: vi.fn() }
    await proxyAuthenticatedBff(new Request('http://localhost/api/test'), {
      path: '/api/users/me',
      cookieStore: cookieStore as never,
    })

    expect(mocks.cookies).not.toHaveBeenCalled()
    expect(mocks.authorized).toHaveBeenCalledWith(cookieStore, '/api/users/me', {
      signal: expect.any(AbortSignal),
    })
  })

  it('maps authenticated backend failures to safe private ProblemDetails', async () => {
    mocks.authorized.mockResolvedValue(
      new Response('<html>upstream failure</html>', {
        status: 500,
        headers: { 'Content-Type': 'text/html', 'X-Internal': 'secret' },
      }),
    )

    const response = await proxyAuthenticatedBff(new Request('http://localhost/api/test'), {
      path: '/api/users/me',
    })

    expect(response.status).toBe(502)
    expect(response.headers.get('Cache-Control')).toBe('private, no-store')
    expect(response.headers.get('Vary')).toBe('Cookie')
    expect(response.headers.get('X-Internal')).toBeNull()
    expect(JSON.stringify(await response.json())).not.toContain('upstream failure')
  })
})
