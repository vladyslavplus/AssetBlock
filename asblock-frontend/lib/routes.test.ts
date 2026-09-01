import { describe, expect, it } from 'vitest'
import { routes, sanitizeInternalReturnUrl } from '@/lib/routes'

describe('sanitizeInternalReturnUrl', () => {
  it('accepts safe internal paths', () => {
    expect(sanitizeInternalReturnUrl('/assets/123')).toBe('/assets/123')
    expect(sanitizeInternalReturnUrl('/library?page=2')).toBe('/library?page=2')
    expect(sanitizeInternalReturnUrl('/collections/abc-def')).toBe('/collections/abc-def')
  })

  it('rejects external URLs and protocol-relative URLs', () => {
    expect(sanitizeInternalReturnUrl('https://evil.com')).toBeNull()
    expect(sanitizeInternalReturnUrl('http://evil.com')).toBeNull()
    expect(sanitizeInternalReturnUrl('//evil.com')).toBeNull()
    expect(sanitizeInternalReturnUrl('/\\evil.com')).toBeNull()
    expect(sanitizeInternalReturnUrl('javascript:alert(1)')).toBeNull()
    expect(sanitizeInternalReturnUrl('')).toBeNull()
    expect(sanitizeInternalReturnUrl(null)).toBeNull()
    expect(sanitizeInternalReturnUrl(undefined)).toBeNull()
  })

  it('rejects strings with control characters', () => {
    expect(sanitizeInternalReturnUrl('/assets\n/test')).toBeNull()
    expect(sanitizeInternalReturnUrl('/assets\r/test')).toBeNull()
  })
})

describe('routes', () => {
  it('encodes dynamic path segments', () => {
    expect(routes.assetDetail('id with spaces')).toBe('/assets/id%20with%20spaces')
    expect(routes.bundleDetail('bundle/123')).toBe('/bundles/bundle%2F123')
    expect(routes.collectionDetail('col#1')).toBe('/collections/col%231')
    expect(routes.userProfile('user@special')).toBe('/users/user%40special')
    expect(routes.sellerAssetEdit('asset 1')).toBe('/sell/assets/asset%201/edit')
    expect(routes.sellerAssetAnalytics('asset 1')).toBe('/sell/analytics/assets/asset%201')
    expect(routes.sellerBundleAnalytics('bundle 1')).toBe('/sell/analytics/bundles/bundle%201')
    expect(routes.assetDownload('ast-1')).toBe('/api/assets/ast-1/download')
    expect(routes.assetDownload('ast-1', 'v-2')).toBe('/api/assets/ast-1/download?versionId=v-2')
  })

  it('builds query parameters deterministically', () => {
    expect(routes.assets()).toBe('/assets')
    expect(routes.assets({ category: '3d-models' })).toBe('/assets?category=3d-models')
    expect(routes.assets({ category: '3d-models', query: 'spaceship', page: 2 })).toBe(
      '/assets?category=3d-models&query=spaceship&page=2',
    )
    expect(routes.bundles({ query: 'pack', page: 1 })).toBe('/bundles?query=pack')
    expect(routes.collections({ query: 'curated' })).toBe('/collections?query=curated')
  })

  it('handles auth redirect routes safely', () => {
    expect(routes.login()).toBe('/login')
    expect(routes.login('/library')).toBe('/login?returnUrl=%2Flibrary')
    expect(routes.login('https://attacker.com')).toBe('/login')
    expect(routes.login('//attacker.com')).toBe('/login')

    expect(routes.register('/checkout')).toBe('/register?returnUrl=%2Fcheckout')
    expect(routes.register('//attacker.com')).toBe('/register')

    expect(routes.resetPassword('tok-123')).toBe('/reset-password?token=tok-123')
    expect(routes.verifyEmail('tok-456')).toBe('/verify-email?token=tok-456')
    expect(routes.confirmEmailChange('tok-789')).toBe('/confirm-email-change?token=tok-789')
  })
})
