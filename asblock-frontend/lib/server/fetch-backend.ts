import { AUTH_COOKIE_ACCESS, AUTH_COOKIE_REFRESH } from '@/lib/auth/constants'
import { getServerApiBaseUrl } from '@/lib/http/api-config'
import { isAccessTokenExpired } from '@/lib/server/access-token'
import { clearAuthCookies, type AuthCookieStore } from '@/lib/server/auth-cookies'
import { tryRefreshFromCookies } from '@/lib/server/refresh-session'

function clearStaleAuthCookiesIfNeeded(
  cookieStore: AuthCookieStore,
  persistRefreshedTokens: boolean,
): void {
  if (!persistRefreshedTokens || !cookieStore.get(AUTH_COOKIE_REFRESH)?.value) {
    return
  }
  clearAuthCookies(cookieStore)
}

function resolveBackendUrl(path: string): string {
  if (/^https?:\/\//i.test(path) || path.startsWith('//')) {
    throw new Error(
      `fetchBackend path must be a backend-relative API path, not an absolute URL: ${path}`,
    )
  }
  const base = getServerApiBaseUrl()
  return `${base}${path.startsWith('/') ? path : `/${path}`}`
}

export interface FetchBackendOptions {
  /** Set false when called from a Server Component (cannot mutate cookies). Default true. */
  persistRefreshedTokens?: boolean
}

type FetchBackendMode = 'required' | 'optional'

/**
 * Shared Web API fetch with cookie-based auth and a single refresh retry on 401.
 */
export async function fetchBackend(
  cookieStore: AuthCookieStore,
  path: string,
  init: RequestInit = {},
  mode: FetchBackendMode,
  authOpts: FetchBackendOptions = {},
): Promise<Response> {
  const persistRefreshedTokens = authOpts.persistRefreshedTokens !== false
  const refreshOpts = { persistCookies: persistRefreshedTokens }

  const url = resolveBackendUrl(path)

  const headers = new Headers(init.headers)

  let access = cookieStore.get(AUTH_COOKIE_ACCESS)?.value ?? null
  if (access && isAccessTokenExpired(access)) {
    const rotated = await tryRefreshFromCookies(cookieStore, refreshOpts)
    access = rotated?.accessToken ?? null
  } else if (!access) {
    const rotated = await tryRefreshFromCookies(cookieStore, refreshOpts)
    access = rotated?.accessToken ?? null
  }

  if (mode === 'required' && !access) {
    clearStaleAuthCookiesIfNeeded(cookieStore, persistRefreshedTokens)
    return new Response(null, { status: 401 })
  }

  if (access) {
    headers.set('Authorization', `Bearer ${access}`)
  }

  if (init.body !== undefined && !headers.has('Content-Type')) {
    const isFormData = typeof FormData !== 'undefined' && init.body instanceof FormData
    if (!isFormData) {
      headers.set('Content-Type', 'application/json')
    }
  }

  let res = await fetch(url, { ...init, headers, cache: 'no-store' })

  if (res.status === 401 && access) {
    const rotated = await tryRefreshFromCookies(cookieStore, refreshOpts)
    if (rotated) {
      headers.set('Authorization', `Bearer ${rotated.accessToken}`)
      res = await fetch(url, { ...init, headers, cache: 'no-store' })
    } else if (mode === 'required') {
      clearStaleAuthCookiesIfNeeded(cookieStore, persistRefreshedTokens)
      return res
    }
  }

  return res
}
