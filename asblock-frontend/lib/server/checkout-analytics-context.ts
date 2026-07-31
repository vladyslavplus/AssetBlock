import type { AuthCookieStore } from '@/lib/server/auth-cookies'
import { ensureAnalyticsCookies } from '@/lib/server/analytics-cookies'
import { isTrackingOptedOut } from '@/lib/server/tracking-opt-out'
import type { CheckoutAttributionBrowser } from '@/lib/analytics/telemetry-schemas'

export interface CheckoutAnalyticsContext {
  trackingOptedOut: boolean
  analyticsVisitorId?: string
  analyticsSessionId?: string
  attribution?: CheckoutAttributionBrowser
}

/**
 * Shared privacy + cookie handling for direct/bundle checkout BFF routes.
 * Opt-out skips cookie create/refresh and drops attribution entirely.
 */
export function prepareCheckoutAnalyticsContext(
  request: Request,
  cookieStore: AuthCookieStore,
  options: {
    doNotTrack?: boolean
    attribution?: CheckoutAttributionBrowser
  },
): CheckoutAnalyticsContext {
  const trackingOptedOut = isTrackingOptedOut(request, options.doNotTrack)
  if (trackingOptedOut) {
    return { trackingOptedOut: true }
  }

  let analyticsVisitorId: string | undefined
  let analyticsSessionId: string | undefined
  try {
    const ids = ensureAnalyticsCookies(cookieStore)
    analyticsVisitorId = ids.visitorId
    analyticsSessionId = ids.sessionId
  } catch {
    // Checkout must succeed even when analytics cookies cannot be set.
  }

  return {
    trackingOptedOut: false,
    analyticsVisitorId,
    analyticsSessionId,
    attribution: options.attribution,
  }
}
