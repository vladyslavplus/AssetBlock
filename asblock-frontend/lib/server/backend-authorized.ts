import type { FetchBackendOptions } from '@/lib/server/fetch-backend'
import { fetchBackend } from '@/lib/server/fetch-backend'
import type { AuthCookieStore } from '@/lib/server/auth-cookies'

export type FetchBackendAuthorizedOptions = FetchBackendOptions

/**
 * Calls the AssetBlock Web API with the access token from cookies; refreshes once on 401.
 */
export async function fetchBackendAuthorized(
  cookieStore: AuthCookieStore,
  path: string,
  init: RequestInit = {},
  authOpts: FetchBackendAuthorizedOptions = {},
): Promise<Response> {
  return fetchBackend(cookieStore, path, init, 'required', authOpts)
}
