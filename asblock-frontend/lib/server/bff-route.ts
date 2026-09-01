import 'server-only'

import { cookies } from 'next/headers'
import type { AuthCookieStore } from '@/lib/server/auth-cookies'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import {
  assertSameOrigin,
  forwardAuthenticatedBackendResponse,
  forwardBackendProblem,
  problemResponse,
} from '@/lib/server/bff-http'

export interface AuthenticatedBffProxyOptions {
  path: string
  init?: Omit<RequestInit, 'signal'>
  enforceSameOrigin?: boolean
  cookieStore?: AuthCookieStore
}

/** Shared authenticated JSON proxy flow. Download, CSV, multipart, and auth-cookie routes stay explicit. */
export async function proxyAuthenticatedBff(
  request: Request,
  options: AuthenticatedBffProxyOptions,
): Promise<Response> {
  if (!options.path.startsWith('/api/') || options.path.startsWith('//')) {
    return problemResponse(500, 'ERR_BFF_ROUTE_INVALID', 'The proxy route is misconfigured.')
  }
  if (options.enforceSameOrigin) {
    const originError = assertSameOrigin(request)
    if (originError) return originError
  }
  const store = options.cookieStore ?? (await cookies())
  const response = await fetchBackendAuthorized(store, options.path, {
    ...options.init,
    signal: request.signal,
  })
  if (!response.ok) {
    const safeProblem = await forwardBackendProblem(response)
    const headers = new Headers(safeProblem.headers)
    headers.set('Cache-Control', 'private, no-store')
    headers.set('Vary', 'Cookie')
    return new Response(safeProblem.body, { status: safeProblem.status, headers })
  }
  return forwardAuthenticatedBackendResponse(response)
}
