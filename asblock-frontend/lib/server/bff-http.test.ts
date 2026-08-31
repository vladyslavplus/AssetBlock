import { describe, expect, it } from 'vitest'

import {
  assertSameOrigin,
  forwardAuthenticatedBackendResponse,
  forwardBackendDownloadResponse,
  forwardBackendResponse,
  invalidJsonResponse,
  problemResponse,
} from '@/lib/server/bff-http'

describe('BFF same-origin and response forwarding', () => {
  it('allows a mutating request with a matching Origin', async () => {
    const request = new Request('http://localhost:3000/api/auth/logout', {
      method: 'POST',
      headers: { Origin: 'http://localhost:3000' },
    })
    expect(assertSameOrigin(request)).toBeNull()
  })

  it('rejects a missing Origin', async () => {
    const request = new Request('http://localhost:3000/api/auth/logout', { method: 'POST' })
    const res = assertSameOrigin(request)
    expect(res?.status).toBe(403)
    expect(res).toBeInstanceOf(Response)
    if (!(res instanceof Response)) return
    const body = await res.json()
    expect(body.code).toBe('ERR_ORIGIN_FORBIDDEN')
    expect(body.detail).toBe('A same-origin request is required.')
  })

  it('rejects a foreign Origin', async () => {
    const request = new Request('http://localhost:3000/api/auth/logout', {
      method: 'POST',
      headers: { Origin: 'https://evil.example' },
    })
    const res = assertSameOrigin(request)
    expect(res?.status).toBe(403)
    expect(res).toBeInstanceOf(Response)
    if (!(res instanceof Response)) return
    expect(await res.json()).toMatchObject({
      code: 'ERR_ORIGIN_FORBIDDEN',
      detail: 'Cross-origin requests are not allowed.',
    })
  })

  it('returns a safe 400 for malformed JSON', async () => {
    const res = invalidJsonResponse()
    const body = await res.json()
    expect(res.status).toBe(400)
    expect(JSON.stringify(body)).not.toMatch(/ZodError|stack|at Object/)
    expect(body.detail).toMatch(/valid JSON/i)
  })

  it('forwards only content-type and content-disposition', async () => {
    const backend = new Response('payload', {
      status: 200,
      headers: {
        'Content-Type': 'application/zip',
        'Content-Disposition': 'attachment; filename="ok.zip"',
        'Set-Cookie': 'refresh=secret; HttpOnly',
        Authorization: 'Bearer leaked',
        'X-Internal-Trace': 'abc',
      },
    })
    const forwarded = forwardBackendResponse(backend)
    expect(forwarded.headers.get('Content-Type')).toBe('application/zip')
    expect(forwarded.headers.get('Content-Disposition')).toBe('attachment; filename="ok.zip"')
    expect(forwarded.headers.get('Set-Cookie')).toBeNull()
    expect(forwarded.headers.get('Authorization')).toBeNull()
    expect(forwarded.headers.get('X-Internal-Trace')).toBeNull()
    expect(forwarded.status).toBe(200)
  })

  it('preserves download content headers and sets Cache-Control: no-store', async () => {
    const backend = new Response('csv', {
      status: 200,
      headers: {
        'Content-Type': 'text/csv',
        'Content-Disposition': 'attachment; filename="sales.csv"',
        'Set-Cookie': 'assetblock_rt=leaked',
      },
    })
    const forwarded = forwardBackendDownloadResponse(backend)
    expect(forwarded.headers.get('Content-Type')).toBe('text/csv')
    expect(forwarded.headers.get('Content-Disposition')).toBe('attachment; filename="sales.csv"')
    expect(forwarded.headers.get('Cache-Control')).toBe('no-store')
    expect(forwarded.headers.get('Set-Cookie')).toBeNull()
  })

  it('sets Cache-Control: private, no-store and Vary: Cookie on authenticated responses', async () => {
    const backend = new Response(JSON.stringify({ me: 'user' }), {
      status: 200,
      headers: {
        'Content-Type': 'application/json',
        'Set-Cookie': 'session=leaked',
      },
    })
    const forwarded = forwardAuthenticatedBackendResponse(backend)
    expect(forwarded.headers.get('Content-Type')).toBe('application/json')
    expect(forwarded.headers.get('Cache-Control')).toBe('private, no-store')
    expect(forwarded.headers.get('Vary')).toBe('Cookie')
    expect(forwarded.headers.get('Set-Cookie')).toBeNull()
  })

  it('sets Cache-Control: no-store on 401 and 403 problem responses', () => {
    const unauth = problemResponse(401, 'ERR_UNAUTHORIZED', 'Unauthorized')
    expect(unauth.headers.get('Cache-Control')).toBe('no-store')

    const forbidden = problemResponse(403, 'ERR_FORBIDDEN', 'Forbidden')
    expect(forbidden.headers.get('Cache-Control')).toBe('no-store')

    const badRequest = problemResponse(400, 'ERR_VALIDATION_FAILED', 'Bad request')
    expect(badRequest.headers.get('Cache-Control')).toBeNull()
  })
})
