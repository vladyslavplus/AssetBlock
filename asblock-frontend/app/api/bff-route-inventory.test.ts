import { readdirSync, readFileSync } from 'node:fs'
import { relative, resolve } from 'node:path'
import { describe, expect, it } from 'vitest'

const apiRoot = resolve(process.cwd(), 'app/api')
const specializedAuthenticatedRoutes = [
  'assets/[id]/download/route.ts',
  'auth/signalr-access/route.ts',
  'seller/analytics/sales/export/route.ts',
  'seller/assets/[id]/versions/route.ts',
  'seller/upload/route.ts',
]
const publicAndAuthRoutes = [
  'account/social-platforms/route.ts',
  'analytics/events/route.ts',
  'auth/email-change/confirm/route.ts',
  'auth/email-verification/confirm/route.ts',
  'auth/login/route.ts',
  'auth/logout/route.ts',
  'auth/password-reset/confirm/route.ts',
  'auth/password-reset/request/route.ts',
  'auth/refresh/route.ts',
  'auth/register/route.ts',
  'auth/session/route.ts',
  'payments/capabilities/route.ts',
]

function findRouteFiles(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = resolve(directory, entry.name)
    if (entry.isDirectory()) return findRouteFiles(path)
    return entry.name === 'route.ts' ? [path] : []
  })
}

const routeFiles = findRouteFiles(apiRoot)
const routeSources = routeFiles.map((path) => ({
  path: relative(apiRoot, path).replaceAll('\\', '/'),
  source: readFileSync(path, 'utf8'),
}))

describe('BFF route inventory contract', () => {
  it('classifies every discovered Route Handler', () => {
    const nonSharedRoutes = routeSources
      .filter(
        ({ source }) =>
          !source.includes('proxyAuthenticatedBff') && !source.includes('fetchBackendAuthorized'),
      )
      .map(({ path }) => path)
      .sort()

    expect(nonSharedRoutes).toEqual([...publicAndAuthRoutes].sort())
  })

  it('keeps every ordinary authenticated proxy on the shared route helper', () => {
    const directAuthenticatedRoutes = routeSources
      .filter(({ source }) => source.includes('fetchBackendAuthorized'))
      .map(({ path }) => path)
      .sort()

    expect(directAuthenticatedRoutes).toEqual([...specializedAuthenticatedRoutes].sort())
    expect(
      routeSources.filter(({ source }) => source.includes('proxyAuthenticatedBff')).length,
    ).toBeGreaterThan(40)
  })

  it.each([
    ['assets/[id]/download/route.ts', /forwardBackendDownloadResponse/],
    ['seller/analytics/sales/export/route.ts', /forwardBackendDownloadResponse/],
    ['seller/upload/route.ts', /maxDuration = 300[\s\S]*formData\(\)/],
    ['seller/assets/[id]/versions/route.ts', /maxDuration = 300[\s\S]*formData\(\)/],
    ['auth/signalr-access/route.ts', /hubToken[\s\S]*Cache-Control/],
  ])('%s retains its specialized response or body flow', (path, contract) => {
    const route = routeSources.find((candidate) => candidate.path === path)
    expect(route?.source).toMatch(contract)
  })

  it.each(
    routeSources.filter(({ source }) =>
      /export async function (POST|PUT|PATCH|DELETE)/.test(source),
    ),
  )('$path protects state-changing handlers with the same-origin contract', ({ source }) => {
    expect(source).toMatch(/assertSameOrigin|enforceSameOrigin:\s*true/)
  })

  it.each(routeSources.filter(({ path }) => /\[[^\]]+\]/.test(path)))(
    '$path validates dynamic UUID parameters',
    ({ source }) => {
      expect(source).toContain('parseUuidParam')
    },
  )

  it.each(
    routeSources.filter(
      ({ source }) =>
        source.includes('proxyAuthenticatedBff') || source.includes('fetchBackendAuthorized'),
    ),
  )('$path propagates cancellation through the shared helper or explicitly', ({ source }) => {
    expect(source).toMatch(/proxyAuthenticatedBff|signal:\s*request\??\.signal/)
  })
})
