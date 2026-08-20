import { cookies } from 'next/headers'

import { normalizeAnalyticsReferrerHost } from '@/lib/analytics/analytics-referrer'
import { ingestAnalyticsEventBrowserSchema } from '@/lib/analytics/telemetry-schemas'
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

  // Validate the browser contract before config-dependent forwarding so malformed
  // payloads still return 400 even when trusted IP / signing secret are missing.
  let json: unknown
  try {
    json = await request.json()
  } catch {
    return invalidJsonResponse()
  }

  const parsed = ingestAnalyticsEventBrowserSchema.safeParse(json)
  if (!parsed.success) {
    return zodValidationProblemResponse(parsed.error)
  }

  const clientIp = resolveTrustedClientIp(request)
  if (!clientIp) {
    if (!trustedClientIpUnavailableLogged) {
      trustedClientIpUnavailableLogged = true
      console.warn('[analytics/events] trusted client IP unavailable; skipping analytics forward')
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
  const referrerHost =
    payload.source === 'EXTERNAL' ? normalizeAnalyticsReferrerHost(payload.referrerHost) : null

  const backendBody = {
    eventId: payload.eventId,
    eventType: payload.eventType,
    visitorId,
    sessionId,
    source: payload.source,
    referrerHost,
    deviceClass: payload.deviceClass,
    assetId: 'assetId' in payload ? payload.assetId : null,
    assetVersionId: 'assetVersionId' in payload ? payload.assetVersionId : null,
    bundleId: 'bundleId' in payload ? payload.bundleId : null,
    collectionId: 'collectionId' in payload ? payload.collectionId : null,
  }

  try {
    const res = await fetchBackendOptionalAuth(store, '/api/analytics/events', {
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
          '[analytics/events] backend rejected analytics BFF signature; telemetry forwarding failed (check shared signing secret)',
        )
      }
      // Still 202 to the browser — telemetry must not affect UX.
    }
  } catch {
    // Best-effort telemetry — never surface errors to the client.
  }

  return new Response(null, { status: 202 })
}
