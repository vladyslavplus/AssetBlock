import { cookies } from 'next/headers'
import { createCheckoutRequestSchema } from '@/lib/payments/payments-schemas'
import { prepareCheckoutAnalyticsContext } from '@/lib/server/checkout-analytics-context'
import {
  assertSameOrigin,
  invalidJsonResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'

export async function POST(request: Request) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  let json: unknown
  try {
    json = await request.json()
  } catch {
    return invalidJsonResponse()
  }
  const parsed = createCheckoutRequestSchema.safeParse(json)
  if (!parsed.success) {
    return zodValidationProblemResponse(parsed.error)
  }

  const store = await cookies()
  const analytics = prepareCheckoutAnalyticsContext(request, store, {
    doNotTrack: parsed.data.doNotTrack,
    attribution: parsed.data.attribution,
  })

  const backendBody = {
    assetId: parsed.data.assetId,
    ...(!analytics.trackingOptedOut && analytics.attribution
      ? { attribution: analytics.attribution }
      : {}),
    ...(!analytics.trackingOptedOut && analytics.analyticsVisitorId
      ? { analyticsVisitorId: analytics.analyticsVisitorId }
      : {}),
    ...(!analytics.trackingOptedOut && analytics.analyticsSessionId
      ? { analyticsSessionId: analytics.analyticsSessionId }
      : {}),
  }

  return proxyAuthenticatedBff(request, {
    path: '/api/payments/checkout',
    cookieStore: store,
    init: {
      method: 'POST',
      body: JSON.stringify(backendBody),
      headers: { 'Content-Type': 'application/json' },
    },
  })
}
