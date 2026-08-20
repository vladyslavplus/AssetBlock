import 'server-only'

import type { AuthCookieStore } from '@/lib/server/auth-cookies'

export const ANALYTICS_COOKIE_VISITOR = 'ab_vid'
export const ANALYTICS_COOKIE_SESSION = 'ab_sid'

/** Visitor cookie lifetime: 365 days. */
const VISITOR_MAX_AGE_SECONDS = 365 * 24 * 60 * 60

/** Session cookie lifetime: 30 minutes (sliding refresh on analytics activity). */
const SESSION_MAX_AGE_SECONDS = 30 * 60

function cookieSecureFlag(): boolean {
  return process.env.NODE_ENV === 'production'
}

function isUuid(value: string | undefined): value is string {
  if (!value) return false
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(
    value,
  )
}

export interface AnalyticsCookieIds {
  visitorId: string
  sessionId: string
}

/**
 * Ensures analytics visitor/session cookies exist and refreshes their sliding expiry on activity.
 * Call from analytics event ingestion and checkout BFF routes only.
 */
export function ensureAnalyticsCookies(cookieStore: AuthCookieStore): AnalyticsCookieIds {
  const secure = cookieSecureFlag()
  const cookieBase = {
    httpOnly: true,
    secure,
    sameSite: 'lax' as const,
    path: '/',
  }

  let visitorId = cookieStore.get(ANALYTICS_COOKIE_VISITOR)?.value
  if (!isUuid(visitorId)) {
    visitorId = crypto.randomUUID()
  }

  let sessionId = cookieStore.get(ANALYTICS_COOKIE_SESSION)?.value
  if (!isUuid(sessionId)) {
    sessionId = crypto.randomUUID()
  }

  cookieStore.set(ANALYTICS_COOKIE_VISITOR, visitorId, {
    ...cookieBase,
    maxAge: VISITOR_MAX_AGE_SECONDS,
  })
  cookieStore.set(ANALYTICS_COOKIE_SESSION, sessionId, {
    ...cookieBase,
    maxAge: SESSION_MAX_AGE_SECONDS,
  })

  return { visitorId, sessionId }
}
