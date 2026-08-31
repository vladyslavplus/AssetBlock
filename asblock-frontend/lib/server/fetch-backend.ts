import { AUTH_COOKIE_ACCESS, AUTH_COOKIE_REFRESH } from '@/lib/auth/constants'
import { getServerApiBaseUrl } from '@/lib/http/api-config'
import { isAccessTokenExpired } from '@/lib/server/access-token'
import { clearAuthCookies, type AuthCookieStore } from '@/lib/server/auth-cookies'
import { problemResponse } from '@/lib/server/bff-http'
import { tryRefreshFromCookies } from '@/lib/server/refresh-session'

export const DEFAULT_BACKEND_TIMEOUT_MS = 30_000
export const LONG_RUNNING_BACKEND_TIMEOUT_MS = 300_000

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
  /** Request timeout budget in milliseconds. Defaults to 30,000 ms (30 s). */
  timeoutMs?: number
}

type FetchBackendMode = 'required' | 'optional'

export interface TimeoutContext {
  signal: AbortSignal
  isTimeout: () => boolean
  cleanup: () => void
}

export function createTimeoutContext(
  timeoutMs: number,
  callerSignal?: AbortSignal | null,
): TimeoutContext {
  const controller = new AbortController()
  let timedOut = false

  const timer = setTimeout(() => {
    timedOut = true
    controller.abort('timeout')
  }, timeoutMs)

  const onCallerAbort = () => {
    controller.abort(callerSignal?.reason)
  }

  if (callerSignal) {
    if (callerSignal.aborted) {
      clearTimeout(timer)
      controller.abort(callerSignal.reason)
    } else {
      callerSignal.addEventListener('abort', onCallerAbort, { once: true })
    }
  }

  const cleanup = () => {
    clearTimeout(timer)
    if (callerSignal) {
      callerSignal.removeEventListener('abort', onCallerAbort)
    }
  }

  return {
    signal: controller.signal,
    isTimeout: () => timedOut || controller.signal.reason === 'timeout',
    cleanup,
  }
}

/**
 * Public unauthenticated server-side fetch with bounded timeout and safe error mapping.
 */
export async function fetchBackendPublic(
  path: string,
  init: RequestInit = {},
  options: { timeoutMs?: number } = {},
): Promise<Response> {
  const url = resolveBackendUrl(path)
  const timeoutMs = options.timeoutMs ?? DEFAULT_BACKEND_TIMEOUT_MS
  const timeoutCtx = createTimeoutContext(timeoutMs, init.signal)

  try {
    return await fetch(url, {
      ...init,
      cache: 'no-store',
      signal: timeoutCtx.signal,
    })
  } catch (error: unknown) {
    if (timeoutCtx.isTimeout()) {
      return problemResponse(504, 'ERR_GATEWAY_TIMEOUT', 'The backend request timed out.')
    }

    if (init.signal?.aborted) {
      throw error
    }

    return problemResponse(502, 'ERR_GATEWAY_ERROR', 'The service is temporarily unavailable.')
  } finally {
    timeoutCtx.cleanup()
  }
}

/**
 * Shared Web API fetch with cookie-based auth, bounded timeout, and a single refresh retry on 401.
 * Both the initial request and optional refresh share a single total timeout budget.
 */
export async function fetchBackend(
  cookieStore: AuthCookieStore,
  path: string,
  init: RequestInit = {},
  mode: FetchBackendMode,
  authOpts: FetchBackendOptions = {},
): Promise<Response> {
  const persistRefreshedTokens = authOpts.persistRefreshedTokens !== false
  const timeoutMs = authOpts.timeoutMs ?? DEFAULT_BACKEND_TIMEOUT_MS
  const timeoutCtx = createTimeoutContext(timeoutMs, init.signal)
  const refreshOpts = { persistCookies: persistRefreshedTokens, signal: timeoutCtx.signal }

  const url = resolveBackendUrl(path)
  const headers = new Headers(init.headers)

  try {
    let access = cookieStore.get(AUTH_COOKIE_ACCESS)?.value ?? null
    if (!access || isAccessTokenExpired(access)) {
      const refreshRes = await tryRefreshFromCookies(cookieStore, refreshOpts)
      if (refreshRes.kind === 'success') {
        access = refreshRes.tokens.accessToken
      } else if (refreshRes.kind === 'rate_limited') {
        return problemResponse(
          429,
          'ERR_RATE_LIMIT_EXCEEDED',
          'Too many refresh requests.',
          undefined,
          refreshRes.retryAfter ? { 'Retry-After': refreshRes.retryAfter } : undefined,
        )
      } else if (refreshRes.kind === 'protocol_error') {
        if (refreshRes.status === 403) {
          return problemResponse(403, 'ERR_FORBIDDEN', 'Access denied.')
        }
        return problemResponse(502, 'ERR_GATEWAY_ERROR', 'The service response was invalid.')
      } else if (refreshRes.kind === 'timeout') {
        return problemResponse(504, 'ERR_GATEWAY_TIMEOUT', 'The authentication request timed out.')
      } else if (refreshRes.kind === 'network_error') {
        return problemResponse(502, 'ERR_GATEWAY_ERROR', 'The service is temporarily unavailable.')
      } else if (refreshRes.kind === 'caller_abort') {
        if (init.signal?.aborted) {
          throw new DOMException('The user aborted a request.', 'AbortError')
        }
        return problemResponse(504, 'ERR_GATEWAY_TIMEOUT', 'The backend request timed out.')
      } else if (refreshRes.kind === 'rejected') {
        clearStaleAuthCookiesIfNeeded(cookieStore, persistRefreshedTokens)
        access = null
      } else {
        access = null
      }
    }

    if (mode === 'required' && !access) {
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

    let res = await fetch(url, {
      ...init,
      headers,
      cache: 'no-store',
      signal: timeoutCtx.signal,
    })

    if (res.status === 401 && access) {
      const refreshRes = await tryRefreshFromCookies(cookieStore, refreshOpts)
      if (refreshRes.kind === 'success') {
        headers.set('Authorization', `Bearer ${refreshRes.tokens.accessToken}`)
        res = await fetch(url, {
          ...init,
          headers,
          cache: 'no-store',
          signal: timeoutCtx.signal,
        })
      } else if (refreshRes.kind === 'rate_limited') {
        return problemResponse(
          429,
          'ERR_RATE_LIMIT_EXCEEDED',
          'Too many refresh requests.',
          undefined,
          refreshRes.retryAfter ? { 'Retry-After': refreshRes.retryAfter } : undefined,
        )
      } else if (refreshRes.kind === 'protocol_error') {
        if (refreshRes.status === 403) {
          return problemResponse(403, 'ERR_FORBIDDEN', 'Access denied.')
        }
        return problemResponse(502, 'ERR_GATEWAY_ERROR', 'The service response was invalid.')
      } else if (refreshRes.kind === 'timeout') {
        return problemResponse(504, 'ERR_GATEWAY_TIMEOUT', 'The authentication request timed out.')
      } else if (refreshRes.kind === 'network_error') {
        return problemResponse(502, 'ERR_GATEWAY_ERROR', 'The service is temporarily unavailable.')
      } else if (refreshRes.kind === 'caller_abort') {
        if (init.signal?.aborted) {
          throw new DOMException('The user aborted a request.', 'AbortError')
        }
        return problemResponse(504, 'ERR_GATEWAY_TIMEOUT', 'The backend request timed out.')
      } else if (refreshRes.kind === 'rejected') {
        clearStaleAuthCookiesIfNeeded(cookieStore, persistRefreshedTokens)
        if (mode === 'required') {
          return res
        }
      } else if (mode === 'required') {
        return res
      }
    }

    return res
  } catch (error: unknown) {
    if (timeoutCtx.isTimeout()) {
      return problemResponse(504, 'ERR_GATEWAY_TIMEOUT', 'The backend request timed out.')
    }

    // If caller explicitly aborted the request, propagate the abort
    if (init.signal?.aborted) {
      throw error
    }

    return problemResponse(502, 'ERR_GATEWAY_ERROR', 'The service is temporarily unavailable.')
  } finally {
    timeoutCtx.cleanup()
  }
}
