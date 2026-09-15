import { afterEach, describe, expect, it, vi } from 'vitest'

import { POST } from '@/app/api/analytics/recommendation-events/route'
import {
  ANALYTICS_BFF_HEADER_PARTITION,
  ANALYTICS_BFF_HEADER_SIGNATURE,
  ANALYTICS_BFF_HEADER_TIMESTAMP,
} from '@/lib/server/analytics-bff-signature'
import { ANALYTICS_COOKIE_SESSION, ANALYTICS_COOKIE_VISITOR } from '@/lib/server/analytics-cookies'
import { createMemoryCookieStore } from '@/test/cookie-store'

const cookieStore = createMemoryCookieStore()

vi.mock('next/headers', () => ({
  cookies: async () => cookieStore,
}))

vi.mock('server-only', () => ({}))

function makeReq(body: unknown, extraHeaders: Record<string, string> = {}): Request {
  return new Request('http://localhost:3000/api/analytics/recommendation-events', {
    method: 'POST',
    headers: {
      Origin: 'http://localhost:3000',
      'Content-Type': 'application/json',
      ...extraHeaders,
    },
    body: typeof body === 'string' ? body : JSON.stringify(body),
  })
}

describe('recommendation-events BFF route', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    vi.unstubAllEnvs()
    cookieStore.delete(ANALYTICS_COOKIE_VISITOR)
    cookieStore.delete(ANALYTICS_COOKIE_SESSION)
    cookieStore.setCalls.length = 0
  })

  it('returns 202 and skips forwarding when DNT is set', async () => {
    const fetchSpy = vi.fn()
    vi.stubGlobal('fetch', fetchSpy)
    const res = await POST(makeReq({ eventId: 'not-a-uuid' }, { DNT: '1' }))
    expect(res.status).toBe(202)
    expect(fetchSpy).not.toHaveBeenCalled()
  })

  it('returns 400 for a malformed envelope without forwarding', async () => {
    const fetchSpy = vi.fn()
    vi.stubGlobal('fetch', fetchSpy)
    const res = await POST(makeReq({ eventType: 'IMPRESSION' }))
    expect(res.status).toBe(400)
    expect(fetchSpy).not.toHaveBeenCalled()
  })

  it('forwards a valid same-origin event with server-owned ids and signed BFF headers', async () => {
    const backendBaseUrl = 'http://api.test'
    vi.stubEnv('ASSETBLOCK_API_BASE_URL', backendBaseUrl)
    vi.stubEnv('NEXT_PUBLIC_API_BASE_URL', backendBaseUrl)
    vi.stubEnv(
      'ASSETBLOCK_ANALYTICS_BFF_SIGNING_SECRET',
      'test-analytics-bff-signing-secret-0123456789',
    )
    vi.stubEnv('TRUSTED_CLIENT_IP_HEADER', 'x-test-client-ip')

    const visitorId = '11111111-1111-4111-8111-111111111111'
    const sessionId = '22222222-2222-4222-8222-222222222222'
    cookieStore.set(ANALYTICS_COOKIE_VISITOR, visitorId)
    cookieStore.set(ANALYTICS_COOKIE_SESSION, sessionId)
    cookieStore.setCalls.length = 0

    const eventId = '33333333-3333-4333-8333-333333333333'
    const sourceAssetId = '44444444-4444-4444-8444-444444444444'
    const targetAssetId = '55555555-5555-4555-8555-555555555555'
    const exposureId = '66666666-6666-4666-8666-666666666666'
    const exposureToken = 'b'.repeat(64)
    // Browser payload carries no visitorId/sessionId: the BFF owns those ids.
    const browserBody = {
      eventId,
      eventType: 'IMPRESSION',
      sourceAssetId,
      targetAssetId,
      slotPosition: 0,
      exposureId,
      rankingVersion: 'similar-assets-v1-metadata',
      expiresAt: '2026-09-13T12:15:00.000Z',
      exposureToken,
      candidateIds: [targetAssetId],
      deviceClass: 'DESKTOP',
    }

    const fetchMock = vi.fn(
      async (_input: RequestInfo | URL, _init?: RequestInit): Promise<Response> =>
        new Response(null, { status: 202 }),
    )
    vi.stubGlobal('fetch', fetchMock)

    const res = await POST(makeReq(browserBody, { 'x-test-client-ip': '203.0.113.10' }))

    expect(res.status).toBe(202)
    expect(fetchMock).toHaveBeenCalledTimes(1)

    const firstCall = fetchMock.mock.calls[0]
    const [calledUrl, init] = firstCall
    expect(String(calledUrl)).toBe(`${backendBaseUrl}/api/analytics/recommendation-events`)
    expect(init?.method).toBe('POST')

    const forwardedHeaders = new Headers(init?.headers)
    expect(forwardedHeaders.get('Content-Type')).toContain('application/json')
    expect(forwardedHeaders.get(ANALYTICS_BFF_HEADER_PARTITION)).toMatch(/^[0-9a-f]{64}$/)
    expect(forwardedHeaders.get(ANALYTICS_BFF_HEADER_TIMESTAMP)).toMatch(/^\d+$/)
    expect(forwardedHeaders.get(ANALYTICS_BFF_HEADER_SIGNATURE)).toMatch(/^[0-9a-f]{64}$/)

    const backendBody = JSON.parse(String(init?.body)) as Record<string, unknown>
    expect(backendBody.eventId).toBe(eventId)
    expect(backendBody.eventType).toBe('IMPRESSION')
    expect(backendBody.sourceAssetId).toBe(sourceAssetId)
    expect(backendBody.targetAssetId).toBe(targetAssetId)
    expect(backendBody.slotPosition).toBe(0)
    expect(backendBody.exposureId).toBe(exposureId)
    expect(backendBody.rankingVersion).toBe('similar-assets-v1-metadata')
    expect(backendBody.exposureToken).toBe(exposureToken)
    expect(backendBody.candidateIds).toEqual([targetAssetId])
    expect(backendBody.deviceClass).toBe('DESKTOP')
    // Server-owned ids win: the browser never supplies visitorId/sessionId.
    expect(backendBody.visitorId).toBe(visitorId)
    expect(backendBody.sessionId).toBe(sessionId)
  })
})
