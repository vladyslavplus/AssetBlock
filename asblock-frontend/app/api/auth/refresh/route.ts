import { cookies } from 'next/headers'
import { NextResponse } from 'next/server'
import { clearAuthCookies } from '@/lib/server/auth-cookies'
import { assertSameOrigin, problemResponse } from '@/lib/server/bff-http'
import { tryRefreshFromCookies } from '@/lib/server/refresh-session'

/**
 * Proactive refresh: rotates tokens using the httpOnly refresh cookie.
 */
export async function POST(request: Request) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const store = await cookies()
  const result = await tryRefreshFromCookies(store, { signal: request.signal })
  if (result.kind === 'success') {
    return NextResponse.json({ ok: true })
  }
  if (result.kind === 'rate_limited') {
    return problemResponse(
      429,
      'ERR_RATE_LIMIT_EXCEEDED',
      'Too many refresh requests.',
      undefined,
      result.retryAfter ? { 'Retry-After': result.retryAfter } : undefined,
    )
  }
  if (result.kind === 'protocol_error') {
    if (result.status === 403) {
      return problemResponse(403, 'ERR_FORBIDDEN', 'Access denied.')
    }
    return problemResponse(502, 'ERR_GATEWAY_ERROR', 'The service response was invalid.')
  }
  if (result.kind === 'timeout') {
    return problemResponse(504, 'ERR_GATEWAY_TIMEOUT', 'The authentication request timed out.')
  }
  if (result.kind === 'network_error') {
    return problemResponse(502, 'ERR_GATEWAY_ERROR', 'The service is temporarily unavailable.')
  }
  if (result.kind === 'caller_abort') {
    return problemResponse(499, 'ERR_CLIENT_CLOSED_REQUEST', 'Client closed request.')
  }
  if (result.kind === 'rejected') {
    clearAuthCookies(store)
  }
  return problemResponse(401, 'ERR_UNAUTHORIZED', 'Unauthorized')
}
