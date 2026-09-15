import { cookies } from 'next/headers'

import { ingestRecommendationEventBrowserSchema } from '@/lib/analytics/telemetry-schemas'
import {
  ANALYTICS_BFF_HEADER_PARTITION,
  ANALYTICS_BFF_HEADER_SIGNATURE,
  ANALYTICS_BFF_HEADER_TIMESTAMP,
  createAnalyticsBffRateLimitHeaders,
} from '@/lib/server/analytics-bff-signature'
import { ensureAnalyticsCookies } from '@/lib/server/analytics-cookies'
import { fetchBackendOptionalAuth } from '@/lib/server/fetch-backend-optional-auth'
import {
  assertSameOrigin,
  invalidJsonResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'
import { isTrackingOptedOut } from '@/lib/server/tracking-opt-out'
import { resolveTrustedClientIp } from '@/lib/server/trusted-client-ip'

let trustedClientIpUnavailableLogged = false
let backendSignatureRejectedLogged = false

export async function POST(request: Request) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  if (isTrackingOptedOut(request)) {
    return new Response(null, { status: 202 })
  }

  let json: unknown
  try {
    json = await request.json()
  } catch {
    return invalidJsonResponse()
  }

  const parsed = ingestRecommendationEventBrowserSchema.safeParse(json)
  if (!parsed.success) {
    return zodValidationProblemResponse(parsed.error)
  }

  const clientIp = resolveTrustedClientIp(request)
  if (!clientIp) {
    if (!trustedClientIpUnavailableLogged) {
      trustedClientIpUnavailableLogged = true
      console.warn(
        '[analytics/recommendation-events] trusted client IP unavailable; skipping recommendation forward',
      )
    }
    return new Response(null, { status: 202 })
  }

  const rateLimitHeaders = createAnalyticsBffRateLimitHeaders(clientIp)
  if (!rateLimitHeaders) {
    return new Response(null, { status: 202 })
  }

  const store = await cookies()
  let visitorId: string
  let sessionId: string
  try {
    ;({ visitorId, sessionId } = ensureAnalyticsCookies(store))
  } catch {
    return new Response(null, { status: 202 })
  }

  const payload = parsed.data
  const backendBody = {
    eventId: payload.eventId,
    eventType: payload.eventType,
    visitorId,
    sessionId,
    sourceAssetId: payload.sourceAssetId,
    targetAssetId: payload.targetAssetId,
    slotPosition: payload.slotPosition,
    exposureId: payload.exposureId,
    rankingVersion: payload.rankingVersion,
    expiresAt: payload.expiresAt,
    exposureToken: payload.exposureToken,
    candidateIds: payload.candidateIds,
    deviceClass: payload.deviceClass,
  }

  try {
    const res = await fetchBackendOptionalAuth(store, '/api/analytics/recommendation-events', {
      method: 'POST',
      body: JSON.stringify(backendBody),
      headers: {
        'Content-Type': 'application/json',
        [ANALYTICS_BFF_HEADER_PARTITION]: rateLimitHeaders.partition,
        [ANALYTICS_BFF_HEADER_TIMESTAMP]: rateLimitHeaders.timestamp,
        [ANALYTICS_BFF_HEADER_SIGNATURE]: rateLimitHeaders.signature,
      },
    })

    if (res.status === 400) {
      const body = await res.text()
      return new Response(body || null, {
        status: 400,
        headers: body ? { 'Content-Type': 'application/json' } : undefined,
      })
    }

    if (res.status === 403) {
      if (!backendSignatureRejectedLogged) {
        backendSignatureRejectedLogged = true
        console.error(
          '[analytics/recommendation-events] backend rejected analytics BFF signature; telemetry forwarding failed (check shared signing secret)',
        )
      }
    }
  } catch {
    // Best-effort telemetry — never surface errors to the client.
  }

  return new Response(null, { status: 202 })
}
