import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { AUTH_COOKIE_ACCESS, AUTH_COOKIE_REFRESH } from '@/lib/auth/constants'
import { fetchBackend, fetchBackendPublic } from '@/lib/server/fetch-backend'
import { tryRefreshFromCookies } from '@/lib/server/refresh-session'
import { createMemoryCookieStore, makeJwt } from '@/test/cookie-store'

const API = 'http://api.test'

function jsonResponse(body: unknown, status = 200, extraHeaders?: HeadersInit): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...extraHeaders },
  })
}

function rotatedTokens() {
  return {
    accessToken: makeJwt(Math.floor(Date.now() / 1000) + 3600),
    refreshToken: 'rotated-refresh',
    accessExpiresAt: new Date(Date.now() + 3600_000).toISOString(),
    refreshExpiresAt: new Date(Date.now() + 86400_000).toISOString(),
  }
}

describe('fetchBackend session refresh', () => {
  beforeEach(() => {
    vi.stubEnv('ASSETBLOCK_API_BASE_URL', API)
    vi.stubEnv('NEXT_PUBLIC_API_BASE_URL', API)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    vi.unstubAllEnvs()
  })

  it('does not refresh when the access JWT is still valid', async () => {
    const access = makeJwt(Math.floor(Date.now() / 1000) + 3600)
    const store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: access,
      [AUTH_COOKIE_REFRESH]: 'refresh-token',
    })
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => {
      const url = String(_input)
      if (url.includes('/api/auth/refresh')) {
        throw new Error('refresh must not run')
      }
      return jsonResponse({ ok: true })
    })
    vi.stubGlobal('fetch', fetchMock)

    const res = await fetchBackend(store, '/api/seller/listings', { method: 'GET' }, 'required')
    expect(res.status).toBe(200)
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const firstCall = fetchMock.mock.calls[0]
    expect(firstCall).toBeDefined()
    const init = (firstCall?.[1] ?? {}) as RequestInit
    expect(new Headers(init.headers).get('Authorization')).toBe(`Bearer ${access}`)
  })

  it('refreshes once and retries the original request after a backend 401', async () => {
    const access = makeJwt(Math.floor(Date.now() / 1000) + 3600)
    const rotated = rotatedTokens()
    const store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: access,
      [AUTH_COOKIE_REFRESH]: 'refresh-token',
    })

    let resourceCalls = 0
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url === `${API}/api/auth/refresh`) {
        return jsonResponse(rotated)
      }
      resourceCalls += 1
      if (resourceCalls === 1) {
        return jsonResponse({ title: 'Unauthorized' }, 401)
      }
      expect(new Headers(init?.headers).get('Authorization')).toBe(`Bearer ${rotated.accessToken}`)
      return jsonResponse({ items: [] })
    })
    vi.stubGlobal('fetch', fetchMock)

    const res = await fetchBackend(store, '/api/seller/listings', { method: 'GET' }, 'required')
    expect(res.status).toBe(200)
    expect(await res.json()).toEqual({ items: [] })
    expect(store.snapshot()[AUTH_COOKIE_ACCESS]).toBe(rotated.accessToken)
    expect(store.snapshot()[AUTH_COOKIE_REFRESH]).toBe('rotated-refresh')
    expect(resourceCalls).toBe(2)
    expect(fetchMock).toHaveBeenCalled()
  })

  it('returns 401 and does not loop when refresh fails after a backend 401', async () => {
    const access = makeJwt(Math.floor(Date.now() / 1000) + 3600)
    const store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: access,
      [AUTH_COOKIE_REFRESH]: 'refresh-token',
    })
    let resourceCalls = 0
    let refreshCalls = 0
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url === `${API}/api/auth/refresh`) {
        refreshCalls += 1
        return jsonResponse({ code: 'ERR_AUTH_TOKEN_INVALID', title: 'Unauthorized' }, 400)
      }
      resourceCalls += 1
      return jsonResponse({ title: 'Unauthorized' }, 401)
    })

    const res = await fetchBackend(store, '/api/seller/listings', { method: 'GET' }, 'required')
    expect(res.status).toBe(401)
    expect(resourceCalls).toBe(1)
    expect(refreshCalls).toBe(1)
    expect(store.snapshot()[AUTH_COOKIE_ACCESS]).toBeUndefined()
    expect(store.snapshot()[AUTH_COOKIE_REFRESH]).toBeUndefined()
  })

  it('clears cookies when refresh fails on expired access before the backend is called', async () => {
    const expiredAccess = makeJwt(Math.floor(Date.now() / 1000) - 60)
    const store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: expiredAccess,
      [AUTH_COOKIE_REFRESH]: 'refresh-token',
    })
    let resourceCalls = 0
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url === `${API}/api/auth/refresh`) {
        return jsonResponse({ code: 'ERR_AUTH_TOKEN_INVALID', title: 'Unauthorized' }, 400)
      }
      resourceCalls += 1
      return jsonResponse({ ok: true })
    })

    const res = await fetchBackend(store, '/api/seller/listings', { method: 'GET' }, 'required')
    expect(res.status).toBe(401)
    expect(resourceCalls).toBe(0)
    expect(store.snapshot()[AUTH_COOKIE_ACCESS]).toBeUndefined()
    expect(store.snapshot()[AUTH_COOKIE_REFRESH]).toBeUndefined()
  })

  it('does not mutate cookies for anonymous required-auth requests', async () => {
    const store = createMemoryCookieStore()
    const deleteSpy = vi.spyOn(store, 'delete')
    vi.stubGlobal('fetch', async () => {
      throw new Error('backend must not be called without access')
    })

    const res = await fetchBackend(store, '/api/seller/listings', { method: 'GET' }, 'required')
    expect(res.status).toBe(401)
    expect(await res.text()).toBe('')
    expect(deleteSpy).not.toHaveBeenCalled()
    expect(store.setCalls).toHaveLength(0)
  })

  it('does not clear cookies on expired access when persistRefreshedTokens is false', async () => {
    const expiredAccess = makeJwt(Math.floor(Date.now() / 1000) - 60)
    const store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: expiredAccess,
      [AUTH_COOKIE_REFRESH]: 'refresh-token',
    })
    const deleteSpy = vi.spyOn(store, 'delete')
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url === `${API}/api/auth/refresh`) {
        return jsonResponse({ code: 'ERR_AUTH_TOKEN_INVALID', title: 'Unauthorized' }, 400)
      }
      throw new Error('backend must not be called without access')
    })

    const res = await fetchBackend(store, '/api/seller/listings', { method: 'GET' }, 'required', {
      persistRefreshedTokens: false,
    })
    expect(res.status).toBe(401)
    expect(deleteSpy).not.toHaveBeenCalled()
    expect(store.snapshot()[AUTH_COOKIE_ACCESS]).toBe(expiredAccess)
    expect(store.snapshot()[AUTH_COOKIE_REFRESH]).toBe('refresh-token')
  })

  it('does not clear cookies on failed refresh when persistRefreshedTokens is false', async () => {
    const access = makeJwt(Math.floor(Date.now() / 1000) + 3600)
    const store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: access,
      [AUTH_COOKIE_REFRESH]: 'refresh-token',
    })
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url === `${API}/api/auth/refresh`) {
        return jsonResponse({ code: 'ERR_AUTH_TOKEN_INVALID', title: 'Unauthorized' }, 400)
      }
      return jsonResponse({ title: 'Unauthorized' }, 401)
    })

    const res = await fetchBackend(store, '/api/seller/listings', { method: 'GET' }, 'required', {
      persistRefreshedTokens: false,
    })
    expect(res.status).toBe(401)
    expect(store.snapshot()[AUTH_COOKIE_ACCESS]).toBe(access)
    expect(store.snapshot()[AUTH_COOKIE_REFRESH]).toBe('refresh-token')
  })

  it('deduplicates concurrent refresh calls into a single network request', async () => {
    const expiredAccess = makeJwt(Math.floor(Date.now() / 1000) - 60)
    const store1 = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: expiredAccess,
      [AUTH_COOKIE_REFRESH]: 'same-refresh-token',
    })
    const store2 = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: expiredAccess,
      [AUTH_COOKIE_REFRESH]: 'same-refresh-token',
    })

    let refreshCalls = 0
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url === `${API}/api/auth/refresh`) {
        refreshCalls++
        await new Promise((r) => setTimeout(r, 20))
        return jsonResponse(rotatedTokens(), 200)
      }
      return jsonResponse({ ok: true }, 200)
    })

    const [res1, res2] = await Promise.all([
      fetchBackend(store1, '/api/seller/listings', { method: 'GET' }, 'required'),
      fetchBackend(store2, '/api/seller/listings', { method: 'GET' }, 'required'),
    ])

    expect(res1.status).toBe(200)
    expect(res2.status).toBe(200)
    expect(refreshCalls).toBe(1)
  })

  it('aborts never-resolving fetch after 10s timeout and cleans up in-flight map for subsequent calls', async () => {
    vi.useFakeTimers()
    try {
      const store = createMemoryCookieStore({
        [AUTH_COOKIE_REFRESH]: 'timeout-token',
      })

      let fetchCount = 0
      vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input)
        if (url === `${API}/api/auth/refresh`) {
          fetchCount++
          if (fetchCount === 1) {
            return new Promise((_, reject) => {
              init?.signal?.addEventListener('abort', () => {
                reject(new DOMException('The operation was aborted', 'AbortError'))
              })
            })
          }
          return jsonResponse(rotatedTokens(), 200)
        }
        return jsonResponse({ ok: true }, 200)
      })

      const firstCallPromise = tryRefreshFromCookies(store)
      await vi.advanceTimersByTimeAsync(10_000)
      const firstResult = await firstCallPromise
      expect(firstResult.kind).toBe('timeout')
      expect(fetchCount).toBe(1)

      // Subsequent call with the same token key makes a new network request, proving inFlightRefreshes was cleaned up
      const secondResult = await tryRefreshFromCookies(store)
      expect(secondResult.kind).toBe('success')
      expect(fetchCount).toBe(2)
    } finally {
      vi.useRealTimers()
    }
  })

  it('preflight refresh timeout returns 504 ProblemDetails and does not clear cookies', async () => {
    const expiredAccess = makeJwt(Math.floor(Date.now() / 1000) - 60)
    const store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: expiredAccess,
      [AUTH_COOKIE_REFRESH]: 'refresh-token',
    })

    vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url === `${API}/api/auth/refresh`) {
        return new Promise((_, reject) => {
          init?.signal?.addEventListener('abort', () => {
            const err = new Error('Refresh timeout')
            err.name = 'AbortError'
            reject(err)
          })
        })
      }
      return jsonResponse({ ok: true })
    })

    const res = await fetchBackend(store, '/api/seller/listings', { method: 'GET' }, 'required', {
      timeoutMs: 50,
    })
    expect(res.status).toBe(504)
    const body = await res.json()
    expect(body.code).toBe('ERR_GATEWAY_TIMEOUT')
    // Cookies must NOT be cleared on timeout!
    expect(store.snapshot()[AUTH_COOKIE_REFRESH]).toBe('refresh-token')
  })

  it('post-401 refresh timeout returns 504 ProblemDetails and does not clear cookies', async () => {
    const validAccess = makeJwt(Math.floor(Date.now() / 1000) + 3600)
    const store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: validAccess,
      [AUTH_COOKIE_REFRESH]: 'refresh-token',
    })

    let initialCall = true
    vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url === `${API}/api/auth/refresh`) {
        return new Promise((_, reject) => {
          init?.signal?.addEventListener('abort', () => {
            const err = new Error('Refresh timeout')
            err.name = 'AbortError'
            reject(err)
          })
        })
      }
      if (initialCall) {
        initialCall = false
        return jsonResponse({ title: 'Unauthorized' }, 401)
      }
      return jsonResponse({ ok: true })
    })

    const res = await fetchBackend(store, '/api/seller/listings', { method: 'GET' }, 'required', {
      timeoutMs: 50,
    })
    expect(res.status).toBe(504)
    const body = await res.json()
    expect(body.code).toBe('ERR_GATEWAY_TIMEOUT')
    expect(store.snapshot()[AUTH_COOKIE_REFRESH]).toBe('refresh-token')
  })

  it('caller abort during preflight refresh returns 499 and does not wipe cookies', async () => {
    const expiredAccess = makeJwt(Math.floor(Date.now() / 1000) - 60)
    const store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: expiredAccess,
      [AUTH_COOKIE_REFRESH]: 'refresh-token',
    })

    const callerController = new AbortController()
    vi.stubGlobal('fetch', async () => {
      return new Promise(() => {})
    })

    const promise = fetchBackend(
      store,
      '/api/seller/listings',
      { method: 'GET', signal: callerController.signal },
      'required',
      { timeoutMs: 10_000 },
    )

    callerController.abort()
    const response = await promise
    expect(response.status).toBe(499)
    expect((await response.json()).code).toBe('ERR_CLIENT_CLOSED_REQUEST')
    expect(store.snapshot()[AUTH_COOKIE_REFRESH]).toBe('refresh-token')
  })

  it('concurrent callers with separate signals: one aborting does not cancel the shared refresh for the other', async () => {
    const expiredAccess = makeJwt(Math.floor(Date.now() / 1000) - 60)
    const store1 = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: expiredAccess,
      [AUTH_COOKIE_REFRESH]: 'shared-refresh-token',
    })
    const store2 = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: expiredAccess,
      [AUTH_COOKIE_REFRESH]: 'shared-refresh-token',
    })

    const caller1Controller = new AbortController()
    const caller2Controller = new AbortController()

    let refreshCallCount = 0
    const rotated = rotatedTokens()
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url === `${API}/api/auth/refresh`) {
        refreshCallCount++
        await new Promise((r) => setTimeout(r, 50))
        return jsonResponse(rotated, 200)
      }
      return jsonResponse({ ok: true, caller: 'success' }, 200)
    })

    const promise1 = fetchBackend(
      store1,
      '/api/seller/listings',
      { method: 'GET', signal: caller1Controller.signal },
      'required',
    )
    const promise2 = fetchBackend(
      store2,
      '/api/seller/listings',
      { method: 'GET', signal: caller2Controller.signal },
      'required',
    )

    // Abort caller 1 immediately while refresh is in flight
    caller1Controller.abort()

    const res1 = await promise1
    expect(res1.status).toBe(499)

    // Caller 2 should successfully finish and get rotated tokens
    const res2 = await promise2
    expect(res2.status).toBe(200)
    expect(refreshCallCount).toBe(1)
    expect(store2.snapshot()[AUTH_COOKIE_ACCESS]).toBe(rotated.accessToken)
  })

  it('single caller abort during refresh cancels the underlying backend refresh request', async () => {
    const expiredAccess = makeJwt(Math.floor(Date.now() / 1000) - 60)
    const store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: expiredAccess,
      [AUTH_COOKIE_REFRESH]: 'single-abort-refresh-token',
    })

    const callerController = new AbortController()
    let underlyingSignal: AbortSignal | undefined

    vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url === `${API}/api/auth/refresh`) {
        underlyingSignal = init?.signal as AbortSignal
        return new Promise<Response>((_, reject) => {
          underlyingSignal?.addEventListener('abort', () => {
            reject(new DOMException('The operation was aborted', 'AbortError'))
          })
        })
      }
      return jsonResponse({ ok: true })
    })

    const promise = fetchBackend(
      store,
      '/api/seller/listings',
      { method: 'GET', signal: callerController.signal },
      'required',
    )

    // Wait a tick so fetch starts and registers underlying signal
    await new Promise((r) => setTimeout(r, 5))
    expect(underlyingSignal).toBeDefined()
    expect(underlyingSignal?.aborted).toBe(false)

    callerController.abort()
    const response = await promise
    expect(response.status).toBe(499)

    // Underlying signal MUST be aborted when the last/only waiter aborts
    expect(underlyingSignal?.aborted).toBe(true)
    expect(store.snapshot()[AUTH_COOKIE_REFRESH]).toBe('single-abort-refresh-token')
  })

  it('abort A -> create B before settlement A -> A settles -> C joins B: total backend calls is exactly 2', async () => {
    const expiredAccess = makeJwt(Math.floor(Date.now() / 1000) - 60)
    const storeA = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: expiredAccess,
      [AUTH_COOKIE_REFRESH]: 'race-token',
    })
    const storeB = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: expiredAccess,
      [AUTH_COOKIE_REFRESH]: 'race-token',
    })
    const storeC = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: expiredAccess,
      [AUTH_COOKIE_REFRESH]: 'race-token',
    })

    const controllerA = new AbortController()
    let refreshCalls = 0
    let resolveSettlementA: (() => void) | undefined
    const rotated = rotatedTokens()

    vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url === `${API}/api/auth/refresh`) {
        refreshCalls++
        const callIndex = refreshCalls
        if (callIndex === 1) {
          // Call A: hangs until resolveSettlementA is triggered
          return new Promise<Response>((_, reject) => {
            init?.signal?.addEventListener('abort', () => {
              // Simulate delayed network settlement after abort
              setTimeout(() => {
                reject(new DOMException('The operation was aborted', 'AbortError'))
                resolveSettlementA?.()
              }, 20)
            })
          })
        }
        // Call B: completes after delay
        await new Promise((r) => setTimeout(r, 100))
        return jsonResponse(rotated, 200)
      }
      return jsonResponse({ ok: true, call: 'api' }, 200)
    })

    // 1. Caller A starts
    const promiseA = fetchBackend(
      storeA,
      '/api/seller/listings',
      { method: 'GET', signal: controllerA.signal },
      'required',
    )
    await new Promise((r) => setTimeout(r, 5))
    expect(refreshCalls).toBe(1)

    // 2. Caller A aborts
    controllerA.abort()
    const responseA = await promiseA
    expect(responseA.status).toBe(499)

    // 3. Caller B starts immediately while A's aborted underlying promise is still settling
    const promiseB = fetchBackend(storeB, '/api/seller/listings', { method: 'GET' }, 'required')
    await new Promise((r) => setTimeout(r, 5))
    expect(refreshCalls).toBe(2)

    // 4. Wait for A's underlying fetch to settle
    await new Promise<void>((r) => {
      resolveSettlementA = r
    })
    await new Promise((r) => setTimeout(r, 10))

    // 5. Caller C arrives after A settled. Because of identity guard, B is NOT deleted, so C joins B!
    const promiseC = fetchBackend(storeC, '/api/seller/listings', { method: 'GET' }, 'required')

    const [resB, resC] = await Promise.all([promiseB, promiseC])
    expect(resB.status).toBe(200)
    expect(resC.status).toBe(200)

    // Total refresh network calls MUST be exactly 2 (A and B), not 3!
    expect(refreshCalls).toBe(2)
    expect(storeB.snapshot()[AUTH_COOKIE_ACCESS]).toBe(rotated.accessToken)
    expect(storeC.snapshot()[AUTH_COOKIE_ACCESS]).toBe(rotated.accessToken)
  })

  it('preflight refresh with 400 without ERR_AUTH_TOKEN_INVALID returns 502 without clearing cookies', async () => {
    const expiredAccess = makeJwt(Math.floor(Date.now() / 1000) - 60)
    const store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: expiredAccess,
      [AUTH_COOKIE_REFRESH]: 'refresh-token',
    })

    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url === `${API}/api/auth/refresh`) {
        return jsonResponse({ code: 'ERR_GENERIC_BAD_REQUEST', detail: 'Invalid parameters' }, 400)
      }
      return jsonResponse({ ok: true })
    })

    const res = await fetchBackend(store, '/api/seller/listings', { method: 'GET' }, 'required')
    expect(res.status).toBe(502)
    const body = await res.json()
    expect(body.code).toBe('ERR_GATEWAY_ERROR')
    // Cookies must NOT be cleared because code is not ERR_AUTH_TOKEN_INVALID!
    expect(store.snapshot()[AUTH_COOKIE_REFRESH]).toBe('refresh-token')
  })

  it('preflight refresh 429 preserves 429 status and Retry-After header without clearing cookies', async () => {
    const expiredAccess = makeJwt(Math.floor(Date.now() / 1000) - 60)
    const store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: expiredAccess,
      [AUTH_COOKIE_REFRESH]: 'refresh-token',
    })

    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url === `${API}/api/auth/refresh`) {
        return jsonResponse(
          { code: 'ERR_RATE_LIMIT_EXCEEDED', detail: 'Rate limit exceeded' },
          429,
          { 'Retry-After': '60' },
        )
      }
      return jsonResponse({ ok: true })
    })

    const res = await fetchBackend(store, '/api/seller/listings', { method: 'GET' }, 'required')
    expect(res.status).toBe(429)
    expect(res.headers.get('Retry-After')).toBe('60')
    const body = await res.json()
    expect(body.code).toBe('ERR_RATE_LIMIT_EXCEEDED')
    expect(store.snapshot()[AUTH_COOKIE_REFRESH]).toBe('refresh-token')
  })

  it('post-401 refresh 429 preserves 429 status and Retry-After header without clearing cookies', async () => {
    const validAccess = makeJwt(Math.floor(Date.now() / 1000) + 3600)
    const store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: validAccess,
      [AUTH_COOKIE_REFRESH]: 'refresh-token',
    })

    let initialCall = true
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url === `${API}/api/auth/refresh`) {
        return jsonResponse(
          { code: 'ERR_RATE_LIMIT_EXCEEDED', detail: 'Rate limit exceeded' },
          429,
          { 'Retry-After': '45' },
        )
      }
      if (initialCall) {
        initialCall = false
        return jsonResponse({ title: 'Unauthorized' }, 401)
      }
      return jsonResponse({ ok: true })
    })

    const res = await fetchBackend(store, '/api/seller/listings', { method: 'GET' }, 'required')
    expect(res.status).toBe(429)
    expect(res.headers.get('Retry-After')).toBe('45')
    const body = await res.json()
    expect(body.code).toBe('ERR_RATE_LIMIT_EXCEEDED')
    expect(store.snapshot()[AUTH_COOKIE_REFRESH]).toBe('refresh-token')
  })

  it('preflight refresh malformed 200 returns 502 ProblemDetails and does not clear cookies', async () => {
    const expiredAccess = makeJwt(Math.floor(Date.now() / 1000) - 60)
    const store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: expiredAccess,
      [AUTH_COOKIE_REFRESH]: 'refresh-token',
    })

    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url === `${API}/api/auth/refresh`) {
        return jsonResponse({ unexpectedShape: true }, 200)
      }
      return jsonResponse({ ok: true })
    })

    const res = await fetchBackend(store, '/api/seller/listings', { method: 'GET' }, 'required')
    expect(res.status).toBe(502)
    const body = await res.json()
    expect(body.code).toBe('ERR_GATEWAY_ERROR')
    expect(store.snapshot()[AUTH_COOKIE_REFRESH]).toBe('refresh-token')
  })

  it('post-401 refresh malformed 200 returns 502 ProblemDetails and does not clear cookies', async () => {
    const validAccess = makeJwt(Math.floor(Date.now() / 1000) + 3600)
    const store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: validAccess,
      [AUTH_COOKIE_REFRESH]: 'refresh-token',
    })

    let initialCall = true
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url === `${API}/api/auth/refresh`) {
        return jsonResponse({ unexpectedShape: true }, 200)
      }
      if (initialCall) {
        initialCall = false
        return jsonResponse({ title: 'Unauthorized' }, 401)
      }
      return jsonResponse({ ok: true })
    })

    const res = await fetchBackend(store, '/api/seller/listings', { method: 'GET' }, 'required')
    expect(res.status).toBe(502)
    const body = await res.json()
    expect(body.code).toBe('ERR_GATEWAY_ERROR')
    expect(store.snapshot()[AUTH_COOKIE_REFRESH]).toBe('refresh-token')
  })

  it('passes AbortSignal to fetch and cleans up on refresh timeout/abort', async () => {
    const expiredAccess = makeJwt(Math.floor(Date.now() / 1000) - 60)
    const store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: expiredAccess,
      [AUTH_COOKIE_REFRESH]: 'signal-refresh-token',
    })

    let receivedSignal: AbortSignal | undefined
    vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url === `${API}/api/auth/refresh`) {
        receivedSignal = init?.signal as AbortSignal
        return jsonResponse(rotatedTokens(), 200)
      }
      return jsonResponse({ ok: true }, 200)
    })

    const res = await fetchBackend(store, '/api/seller/listings', { method: 'GET' }, 'required')
    expect(res.status).toBe(200)
    expect(receivedSignal).toBeDefined()
  })

  it('throws when given an absolute URL path', async () => {
    const store = createMemoryCookieStore()
    await expect(
      fetchBackend(
        store,
        'https://evil.example/api/seller/listings',
        { method: 'GET' },
        'required',
      ),
    ).rejects.toThrow(
      'fetchBackend path must be a backend-relative API path, not an absolute URL: https://evil.example/api/seller/listings',
    )
  })

  it('returns 504 ProblemDetails when request exceeds timeout budget', async () => {
    const access = makeJwt(Math.floor(Date.now() / 1000) + 3600)
    const store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: access,
      [AUTH_COOKIE_REFRESH]: 'refresh-token',
    })

    vi.stubGlobal('fetch', async (_input: RequestInfo | URL, init?: RequestInit) => {
      return new Promise<Response>((_, reject) => {
        init?.signal?.addEventListener('abort', () => {
          const err = new Error('The operation was aborted.')
          err.name = 'AbortError'
          reject(err)
        })
      })
    })

    const res = await fetchBackend(store, '/api/seller/listings', { method: 'GET' }, 'required', {
      timeoutMs: 50,
    })
    expect(res.status).toBe(504)
    expect(res.headers.get('Content-Type')).toBe('application/problem+json')
    const body = await res.json()
    expect(body.code).toBe('ERR_GATEWAY_TIMEOUT')
    expect(body.detail).toBe('The backend request timed out.')
  })

  it('returns 499 when caller aborts before timeout', async () => {
    const access = makeJwt(Math.floor(Date.now() / 1000) + 3600)
    const store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: access,
      [AUTH_COOKIE_REFRESH]: 'refresh-token',
    })

    const callerController = new AbortController()
    vi.stubGlobal('fetch', async (_input: RequestInfo | URL, init?: RequestInit) => {
      return new Promise<Response>((_, reject) => {
        init?.signal?.addEventListener('abort', () => {
          const err = new Error('Caller aborted')
          err.name = 'AbortError'
          reject(err)
        })
      })
    })

    const fetchPromise = fetchBackend(
      store,
      '/api/seller/listings',
      { method: 'GET', signal: callerController.signal },
      'required',
      { timeoutMs: 10_000 },
    )

    callerController.abort()
    const response = await fetchPromise
    expect(response.status).toBe(499)
    expect((await response.json()).code).toBe('ERR_CLIENT_CLOSED_REQUEST')
  })

  it('returns 499 for a cancelled public BFF request', async () => {
    const callerController = new AbortController()
    vi.stubGlobal('fetch', async (_input: RequestInfo | URL, init?: RequestInit) => {
      return new Promise<Response>((_, reject) => {
        init?.signal?.addEventListener('abort', () => {
          reject(new DOMException('Caller aborted', 'AbortError'))
        })
      })
    })

    const promise = fetchBackendPublic('/api/payments/capabilities', {
      signal: callerController.signal,
    })
    callerController.abort()

    const response = await promise
    expect(response.status).toBe(499)
    expect((await response.json()).code).toBe('ERR_CLIENT_CLOSED_REQUEST')
  })

  it('returns 502 ProblemDetails when network error occurs', async () => {
    const access = makeJwt(Math.floor(Date.now() / 1000) + 3600)
    const store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: access,
      [AUTH_COOKIE_REFRESH]: 'refresh-token',
    })

    vi.stubGlobal('fetch', async () => {
      throw new Error('ECONNREFUSED')
    })

    const res = await fetchBackend(store, '/api/seller/listings', { method: 'GET' }, 'required')
    expect(res.status).toBe(502)
    const body = await res.json()
    expect(body.code).toBe('ERR_GATEWAY_ERROR')
  })

  it('does not replay a one-shot request body when unauthorized retry is disabled', async () => {
    const access = makeJwt(Math.floor(Date.now() / 1000) + 3600)
    const store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: access,
      [AUTH_COOKIE_REFRESH]: 'refresh-token',
    })
    const fetchMock = vi.fn(async () => new Response(null, { status: 401 }))
    vi.stubGlobal('fetch', fetchMock)

    const res = await fetchBackend(
      store,
      '/api/assets/upload',
      { method: 'POST', body: 'one-shot-body' },
      'required',
      { retryOnUnauthorized: false },
    )

    expect(res.status).toBe(401)
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })
})
