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
  /**
   * Optional AbortSignal to propagate caller cancellation or overall deadline budget.
   */
  signal?: AbortSignal
}

export type RefreshResult =
  | { kind: 'success'; tokens: TokensPayload }
  | { kind: 'rejected'; status: number; problem?: unknown }
  | { kind: 'rate_limited'; status: 429; retryAfter?: string | null; problem?: unknown }
  | { kind: 'protocol_error'; status: number; problem?: unknown }
  | { kind: 'timeout' }
  | { kind: 'network_error' }
  | { kind: 'caller_abort' }
  | { kind: 'no_token' }

const REFRESH_TIMEOUT_MS = 10_000

interface InFlightRefresh {
  promise: Promise<{ ok: boolean; status: number; data: unknown; headers?: Headers }>
  controller: AbortController
  waiterCount: number
}

/**
 * Module-level in-flight single-flight map to deduplicate concurrent refresh requests
 * for the same refresh token within the same Next.js process instance.
 * Tracks active waiters: if all waiters abort, the underlying fetch is aborted to avoid
 * losing rotated tokens and triggering token reuse revocation on subsequent requests.
 */
const inFlightRefreshes = new Map<string, InFlightRefresh>()

async function raceWithCallerSignal<T>(
  promise: Promise<T>,
  callerSignal?: AbortSignal,
): Promise<{ aborted: false; value: T } | { aborted: true }> {
  if (!callerSignal) {
    const value = await promise
    return { aborted: false, value }
  }

  if (callerSignal.aborted) {
    return { aborted: true }
  }

  return new Promise<{ aborted: false; value: T } | { aborted: true }>((resolve, reject) => {
    const onAbort = () => {
      resolve({ aborted: true })
    }

    callerSignal.addEventListener('abort', onAbort, { once: true })

    promise
      .then((value) => {
        callerSignal.removeEventListener('abort', onAbort)
        resolve({ aborted: false, value })
      })
      .catch((err) => {
        callerSignal.removeEventListener('abort', onAbort)
        reject(err)
      })
  })
}

/**
 * Calls Web API refresh and optionally persists rotated tokens in httpOnly cookies.
 * Concurrent callers sharing the exact same refresh token share the single in-flight network request.
 * If all waiters abort, the underlying network request is cancelled.
 */
export async function exchangeRefreshToken(
  store: AuthCookieStore,
  refreshToken: string,
  options: RefreshSessionOptions = {},
): Promise<RefreshResult> {
  const persistCookies = options.persistCookies !== false
  const tokenKey = refreshToken.trim()
  if (!tokenKey) {
    return { kind: 'no_token' }
  }

  let inFlight = inFlightRefreshes.get(tokenKey)
  if (inFlight && inFlight.controller.signal.aborted) {
    inFlight = undefined
  }

  if (!inFlight) {
    const controller = new AbortController()
    const entry: InFlightRefresh = {
      promise: Promise.resolve() as unknown as Promise<{
        ok: boolean
        status: number
        data: unknown
        headers?: Headers
      }>,
      controller,
      waiterCount: 0,
    }
    entry.promise = postAuthJson(
      'refresh',
      { refreshToken: tokenKey },
      { signal: controller.signal, timeoutMs: REFRESH_TIMEOUT_MS },
    ).finally(() => {
      if (inFlightRefreshes.get(tokenKey) === entry) {
        inFlightRefreshes.delete(tokenKey)
      }
    })
    inFlight = entry
    inFlightRefreshes.set(tokenKey, inFlight)
  }

  inFlight.waiterCount++

  try {
    const raced = await raceWithCallerSignal(inFlight.promise, options.signal)

    if (raced.aborted) {
      if (options.signal?.reason === 'timeout') {
        return { kind: 'timeout' }
      }
      return { kind: 'caller_abort' }
    }

    const { ok, status, data, headers } = raced.value
    if (ok) {
      const parsed = tokensResponseSchema.safeParse(data)
      if (parsed.success) {
        if (persistCookies) {
          setAuthCookies(store, parsed.data)
        }
        return { kind: 'success', tokens: parsed.data }
      }
      return {
        kind: 'protocol_error',
        status: 502,
        problem: data,
      }
    }

    if (status === 429) {
      const retryAfter = headers?.get('Retry-After') ?? null
      return { kind: 'rate_limited', status: 429, retryAfter, problem: data }
    }

    if (status === 504) {
      return { kind: 'timeout' }
    }

    if (status === 502) {
      return { kind: 'network_error' }
    }

    const errorCode =
      typeof data === 'object' && data !== null && 'code' in data
        ? String((data as { code?: unknown }).code)
        : typeof data === 'object' && data !== null && 'type' in data
          ? String((data as { type?: unknown }).type)
          : ''

    const isTokenInvalid =
      errorCode === 'ERR_AUTH_TOKEN_INVALID' ||
      errorCode.endsWith(':ERR_AUTH_TOKEN_INVALID')

    if (isTokenInvalid) {
      return { kind: 'rejected', status, problem: data }
    }

    return {
      kind: 'protocol_error',
      status: status >= 400 && status < 600 ? status : 502,
      problem: data,
    }
  } catch {
    return { kind: 'network_error' }
  } finally {
    inFlight.waiterCount--
    if (inFlight.waiterCount <= 0) {
      inFlight.controller.abort('all_waiters_aborted')
    }
  }
}

export async function tryRefreshFromCookies(
  store: AuthCookieStore,
  options: RefreshSessionOptions = {},
): Promise<RefreshResult> {
  const rt = store.get(AUTH_COOKIE_REFRESH)?.value
  if (!rt) {
    return { kind: 'no_token' }
  }
  return exchangeRefreshToken(store, rt, options)
}
