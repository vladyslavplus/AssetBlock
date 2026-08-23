import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { AUTH_COOKIE_ACCESS, AUTH_COOKIE_REFRESH } from '@/lib/auth/constants'
import type { TokensPayload } from '@/lib/auth/tokens-schema'
import { clearAuthCookies, setAuthCookies } from '@/lib/server/auth-cookies'
import { createMemoryCookieStore } from '@/test/cookie-store'

function tokens(overrides: Partial<TokensPayload> = {}): TokensPayload {
  return {
    accessToken: 'access-secret-token',
    refreshToken: 'refresh-secret-token',
    accessExpiresAt: '2026-08-23T13:00:00.000Z',
    refreshExpiresAt: '2026-08-30T12:00:00.000Z',
    ...overrides,
  }
}

describe('auth cookies', () => {
  beforeEach(() => {
    vi.useFakeTimers({ now: new Date('2026-08-23T12:00:00.000Z') })
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.unstubAllEnvs()
  })

  it('sets httpOnly, SameSite=lax, path=/, and maxAge from expiry', () => {
    vi.stubEnv('NODE_ENV', 'development')
    const store = createMemoryCookieStore()
    setAuthCookies(store, tokens())

    expect(store.setCalls).toHaveLength(2)
    const access = store.setCalls.find((call) => call.name === AUTH_COOKIE_ACCESS)
    const refresh = store.setCalls.find((call) => call.name === AUTH_COOKIE_REFRESH)
    expect(access?.options).toMatchObject({
      httpOnly: true,
      secure: false,
      sameSite: 'lax',
      path: '/',
      maxAge: 3600,
    })
    expect(refresh?.options).toMatchObject({
      httpOnly: true,
      secure: false,
      sameSite: 'lax',
      path: '/',
      maxAge: 7 * 24 * 3600,
    })
  })

  it('sets Secure in production', () => {
    vi.stubEnv('NODE_ENV', 'production')
    const store = createMemoryCookieStore()
    setAuthCookies(store, tokens())
    expect(store.setCalls[0]?.options).toMatchObject({ secure: true, httpOnly: true })
  })

  it('clears access and refresh cookies', () => {
    const store = createMemoryCookieStore({
      [AUTH_COOKIE_ACCESS]: 'access-secret-token',
      [AUTH_COOKIE_REFRESH]: 'refresh-secret-token',
    })
    clearAuthCookies(store)
    expect(store.snapshot()).toEqual({})
  })
})
