import type { FetchBackendOptions } from '@/lib/server/fetch-backend'
import { fetchBackend } from '@/lib/server/fetch-backend'
import type { AuthCookieStore } from '@/lib/server/auth-cookies'

export type FetchBackendOptionalAuthOptions = FetchBackendOptions

/**
 * Calls the AssetBlock Web API, attaching Authorization when session cookies are present.
 * Does not fail when unauthenticated — suitable for anonymous-capable backend routes.
 */
export async function fetchBackendOptionalAuth(
  cookieStore: AuthCookieStore,
  path: string,
  init: RequestInit = {},
  authOpts: FetchBackendOptionalAuthOptions = {},
): Promise<Response> {
  return fetchBackend(cookieStore, path, init, 'optional', authOpts)
}
