import { describe, expect, it } from 'vitest'
import { NextRequest } from 'next/server'
import { AUTH_COOKIE_ACCESS, AUTH_COOKIE_REFRESH } from '@/lib/auth/constants'
import { proxy } from './proxy'

function makeJwt(payload: Record<string, unknown>): string {
  const encoded = Buffer.from(JSON.stringify(payload), 'utf8').toString('base64url')
  return `eyJhbGciOiJub25lIn0.${encoded}.unsigned`
}

function makeRequest(path: string, cookies: Record<string, string> = {}): NextRequest {
  const request = new NextRequest(`http://localhost:3000${path}`)
  for (const [name, value] of Object.entries(cookies)) {
    request.cookies.set(name, value)
  }
  return request
}

describe('proxy UX guards', () => {
  it('redirects a protected route to login without a refresh cookie', () => {
    const response = proxy(makeRequest('/sell/assets?tab=drafts'))

    expect(response.status).toBe(307)
    expect(response.headers.get('location')).toBe(
      'http://localhost:3000/login?returnUrl=%2Fsell%2Fassets%3Ftab%3Ddrafts',
    )
  })

  it.each([
    { role: 'Admin', claim: 'role' },
    {
      role: 'Admin',
      claim: 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role',
    },
  ])('allows the admin UX route for the $claim role claim', ({ role, claim }) => {
    const response = proxy(
      makeRequest('/admin', {
        [AUTH_COOKIE_REFRESH]: 'refresh',
        [AUTH_COOKIE_ACCESS]: makeJwt({ [claim]: role }),
      }),
    )

    expect(response.status).toBe(200)
    expect(response.headers.get('x-middleware-next')).toBe('1')
  })

  it.each([makeJwt({ role: 'Seller' }), 'not-a-jwt', makeJwt({ role: ['Admin'] })])(
    'redirects a present but non-admin access token away from the admin UX route',
    (accessToken) => {
      const response = proxy(
        makeRequest('/admin', {
          [AUTH_COOKIE_REFRESH]: 'refresh',
          [AUTH_COOKIE_ACCESS]: accessToken,
        }),
      )

      expect(response.status).toBe(307)
      expect(response.headers.get('location')).toBe('http://localhost:3000/')
    },
  )

  it('does not treat the coarse role check as a session requirement for public routes', () => {
    const response = proxy(makeRequest('/assets'))

    expect(response.status).toBe(200)
    expect(response.headers.get('x-middleware-next')).toBe('1')
  })
})
