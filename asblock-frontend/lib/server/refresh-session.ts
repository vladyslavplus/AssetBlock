import { AUTH_COOKIE_REFRESH } from '@/lib/auth/constants'
import { tokensResponseSchema, type TokensPayload } from '@/lib/auth/tokens-schema'
import { postAuthJson } from '@/lib/server/auth-backend'
import { setAuthCookies, type AuthCookieStore } from '@/lib/server/auth-cookies'

export interface RefreshSessionOptions {
  /**
   * When false, returns new tokens but does not call `cookies().set` (required from Server Components).
   * Route Handlers / Server Actions should keep the default (persist).
   */
  persistCookies?: boolean
}

const REFRESH_TIMEOUT_MS = 10_000

/**
 * Module-level in-flight single-flight map to deduplicate concurrent refresh requests
 * for the same refresh token within the same Next.js process instance.
 * Multi-process concurrency is safely coordinated by backend atomic conditional token rotation.
 */
const inFlightRefreshes = new Map<string, Promise<{ ok: boolean; data: unknown }>>()

async function fetchWithTimeout(payload: {
  refreshToken: string
}): Promise<{ ok: boolean; data: unknown }> {
  const controller = new AbortController()
  const timeoutId = setTimeout(() => controller.abort(), REFRESH_TIMEOUT_MS)
  try {
    return await postAuthJson('refresh', payload, { signal: controller.signal })
  } finally {
    clearTimeout(timeoutId)
  }
}

/**
 * Calls Web API refresh and optionally persists rotated tokens in httpOnly cookies.
 * Concurrent callers sharing the exact same refresh token share the single in-flight network request.
 */
export async function exchangeRefreshToken(
  store: AuthCookieStore,
  refreshToken: string,
  options: RefreshSessionOptions = {},
): Promise<TokensPayload | null> {
  const persistCookies = options.persistCookies !== false
  const tokenKey = refreshToken.trim()
  if (!tokenKey) {
    return null
  }

  let refreshPromise = inFlightRefreshes.get(tokenKey)
  if (!refreshPromise) {
    refreshPromise = fetchWithTimeout({ refreshToken: tokenKey }).finally(() => {
      inFlightRefreshes.delete(tokenKey)
    })
    inFlightRefreshes.set(tokenKey, refreshPromise)
  }

  const { ok, data } = await refreshPromise
  if (!ok) {
    return null
  }
  const parsed = tokensResponseSchema.safeParse(data)
  if (!parsed.success) {
    return null
  }
  if (persistCookies) {
    setAuthCookies(store, parsed.data)
  }
  return parsed.data
}

export async function tryRefreshFromCookies(
  store: AuthCookieStore,
  options: RefreshSessionOptions = {},
): Promise<TokensPayload | null> {
  const rt = store.get(AUTH_COOKIE_REFRESH)?.value
  if (!rt) {
    return null
  }
  return exchangeRefreshToken(store, rt, options)
}
