import { describe, expect, it } from 'vitest'
import { parseEnvironment } from './env'

const validEnvironment = {
  NEXT_PUBLIC_API_BASE_URL: ' https://public.example.com/ ',
  ASSETBLOCK_API_BASE_URL: ' http://backend.internal:5088/// ',
  ASSETBLOCK_ANALYTICS_BFF_SIGNING_SECRET: undefined,
  TRUSTED_CLIENT_IP_HEADER: undefined,
}

describe('environment', () => {
  it('parses and normalizes public and server configuration', () => {
    expect(parseEnvironment(validEnvironment)).toEqual({
      publicApiBaseUrl: 'https://public.example.com',
      serverApiBaseUrl: 'http://backend.internal:5088',
      analyticsBffSigningSecret: undefined,
      trustedClientIpHeader: undefined,
    })
  })

  it.each(['NEXT_PUBLIC_API_BASE_URL', 'ASSETBLOCK_API_BASE_URL'] as const)(
    'rejects a missing %s',
    (key) => {
      expect(() => parseEnvironment({ ...validEnvironment, [key]: undefined })).toThrow()
    },
  )

  it('rejects non-HTTP API URLs', () => {
    expect(() =>
      parseEnvironment({ ...validEnvironment, ASSETBLOCK_API_BASE_URL: 'ftp://backend.internal' }),
    ).toThrow(/HTTP/)
  })

  it('rejects a configured analytics secret shorter than 32 characters', () => {
    expect(() =>
      parseEnvironment({
        ...validEnvironment,
        ASSETBLOCK_ANALYTICS_BFF_SIGNING_SECRET: 'too-short',
      }),
    ).toThrow()
  })

  it('normalizes a valid trusted client IP header name', () => {
    expect(
      parseEnvironment({ ...validEnvironment, TRUSTED_CLIENT_IP_HEADER: ' CF-Connecting-IP ' })
        .trustedClientIpHeader,
    ).toBe('cf-connecting-ip')
  })

  it('rejects an invalid trusted client IP header name', () => {
    expect(() =>
      parseEnvironment({ ...validEnvironment, TRUSTED_CLIENT_IP_HEADER: 'not a header' }),
    ).toThrow(/valid HTTP header name/)
  })
})
