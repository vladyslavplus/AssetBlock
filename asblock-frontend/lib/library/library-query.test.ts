import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  fetchLibraryAssetVersions,
  fetchLibraryPurchases,
  LibraryFetchError,
} from '@/lib/library/library-query'
import {
  ASSET_VERSION_PROCESSING_STATUSES,
  type AssetVersionProcessingStatus,
} from '@/lib/library/library-schemas'

const validPurchaseItem = {
  id: '11111111-1111-4111-8111-111111111111',
  orderId: '22222222-2222-4222-8222-222222222222',
  assetId: '33333333-3333-4333-8333-333333333333',
  assetTitle: 'Sci-Fi Modular Pack',
  price: 49.99,
  purchasedAt: '2026-01-15T12:00:00.000Z',
  authorUsername: 'scifidev',
  hasUserReviewed: false,
  purchasedVersionNumber: 1,
  purchasedVersionId: '44444444-4444-4444-8444-444444444444',
  latestEntitledVersionNumber: 2,
  latestEntitledVersionId: '55555555-5555-4555-8555-555555555555',
  hasUpdate: true,
  pricePaid: 49.99,
  currency: 'usd',
  source: 'ASSET' as const,
  bundleId: null,
  bundleTitle: null,
}

const buildVersionSummary = (
  processingStatus: AssetVersionProcessingStatus = 'READY',
  licenseCode: 'PERSONAL' | 'COMMERCIAL' = 'PERSONAL',
) => ({
  id: '44444444-4444-4444-8444-444444444444',
  versionNumber: 1,
  isCurrent: true,
  fileName: 'pack-v1.zip',
  contentLength: 1048576,
  contentSha256: 'a'.repeat(64),
  releaseNotes: 'Initial release',
  createdAt: '2026-01-15T12:00:00.000Z',
  license: {
    code: licenseCode,
    displayName: licenseCode === 'PERSONAL' ? 'Personal Use' : 'Commercial Use',
    templateVersion: '1.0',
    terms: 'License terms snapshot.',
  },
  processingStatus,
  processingErrorCode: null,
  processingErrorSummary: null,
  processingUpdatedAt: null,
})

describe('fetchLibraryPurchases', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('validates and returns paged purchases when response matches schema', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(
            JSON.stringify({
              items: [validPurchaseItem],
              totalCount: 1,
              page: 1,
              pageSize: 12,
            }),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          ),
      ),
    )

    const result = await fetchLibraryPurchases()
    expect(result.ok).toBe(true)
    if (result.ok) {
      expect(result.data.items).toHaveLength(1)
      expect(result.data.items[0]?.assetTitle).toBe('Sci-Fi Modular Pack')
    }
  })

  it('returns 502 error status on malformed server payload', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(
            JSON.stringify({
              items: [{ invalidField: true }],
              totalCount: 'not-a-number',
            }),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          ),
      ),
    )

    const result = await fetchLibraryPurchases()
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.status).toBe(502)
      expect(result.message).toMatch(/invalid library response/i)
    }
  })
})

describe('fetchLibraryAssetVersions', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it.each(ASSET_VERSION_PROCESSING_STATUSES)(
    'validates and returns version summary for backend status %s',
    async (status) => {
      vi.stubGlobal(
        'fetch',
        vi.fn(
          async () =>
            new Response(JSON.stringify([buildVersionSummary(status)]), {
              status: 200,
              headers: { 'Content-Type': 'application/json' },
            }),
        ),
      )

      const versions = await fetchLibraryAssetVersions('33333333-3333-4333-8333-333333333333')
      expect(versions).toHaveLength(1)
      expect(versions[0]?.fileName).toBe('pack-v1.zip')
      expect(versions[0]?.processingStatus).toBe(status)
    },
  )

  it('accepts COMMERCIAL license code', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify([buildVersionSummary('READY', 'COMMERCIAL')]), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )

    const versions = await fetchLibraryAssetVersions('33333333-3333-4333-8333-333333333333')
    expect(versions).toHaveLength(1)
    expect(versions[0]?.license.code).toBe('COMMERCIAL')
  })

  it('rejects invalid license code with 502 error', async () => {
    const invalidLicenseVersion = {
      ...buildVersionSummary('READY'),
      license: {
        code: 'INVALID_LICENSE',
        displayName: 'Invalid',
        templateVersion: '1.0',
        terms: 'Terms',
      },
    }

    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify([invalidLicenseVersion]), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )

    await expect(fetchLibraryAssetVersions('33333333-3333-4333-8333-333333333333')).rejects.toThrow(
      LibraryFetchError,
    )
  })

  it('throws LibraryFetchError(502) on malformed server payload', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify([{ invalid: 123 }]), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )

    await expect(fetchLibraryAssetVersions('33333333-3333-4333-8333-333333333333')).rejects.toThrow(
      LibraryFetchError,
    )
  })
})
