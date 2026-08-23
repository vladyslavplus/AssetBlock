import { describe, expect, it } from 'vitest'

import { bundleItemResponseSchema } from '@/lib/bundles/bundle-schemas'

describe('bundle item licenseCode contract', () => {
  it('accepts PERSONAL and COMMERCIAL strings', () => {
    const base = {
      assetId: '11111111-1111-4111-8111-111111111111',
      title: 'Item',
      listPrice: 10,
      position: 1,
      isAvailable: true,
      unavailableReason: null,
      currentVersionNumber: 1,
      licenseDisplayName: 'Personal',
    }
    expect(bundleItemResponseSchema.parse({ ...base, licenseCode: 'PERSONAL' }).licenseCode).toBe(
      'PERSONAL',
    )
    expect(bundleItemResponseSchema.parse({ ...base, licenseCode: 'COMMERCIAL' }).licenseCode).toBe(
      'COMMERCIAL',
    )
    expect(bundleItemResponseSchema.parse({ ...base, licenseCode: null }).licenseCode).toBeNull()
  })

  it('rejects enum ordinal numbers', () => {
    const base = {
      assetId: '11111111-1111-4111-8111-111111111111',
      title: 'Item',
      listPrice: 10,
      position: 1,
      isAvailable: true,
      unavailableReason: null,
      currentVersionNumber: 1,
      licenseDisplayName: 'Personal',
      licenseCode: 0,
    }
    expect(() => bundleItemResponseSchema.parse(base)).toThrow()
  })
})
