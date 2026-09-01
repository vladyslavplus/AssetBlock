import { afterEach, describe, expect, it, vi } from 'vitest'
import { getBundleDetailCached } from '@/lib/server/bundle-detail-server'

describe('bundle-detail-server loader', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  const validBundleId = '11111111-1111-4111-8111-111111111111'

  const sampleBundle = {
    id: validBundleId,
    revisionId: '22222222-2222-4222-8222-222222222222',
    revisionNumber: 1,
    title: 'Ultimate RPG Pack',
    description: 'Great bundle',
    price: 49.99,
    listPriceTotal: 99.99,
    savingsAmount: 50.0,
    savingsPercent: 50,
    currency: 'USD',
    sellerId: '33333333-3333-4333-8333-333333333333',
    sellerUsername: 'artisan',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: null,
    archivedAt: null,
    isArchived: false,
    isAvailable: true,
    items: [
      {
        assetId: '44444444-4444-4444-8444-444444444444',
        title: 'Hero Model',
        listPrice: 25.0,
        position: 1,
        isAvailable: true,
        unavailableReason: null,
        currentVersionNumber: 1,
        licenseCode: 'STANDARD',
        licenseDisplayName: 'Standard License',
      },
    ],
  }

  it('loads and parses valid bundle detail from backend', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify(sampleBundle), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )

    const result = await getBundleDetailCached(validBundleId)
    expect(result.status).toBe('success')
    if (result.status === 'success') {
      expect(result.bundle.title).toBe('Ultimate RPG Pack')
      expect(result.bundle.items).toHaveLength(1)
    }
  })

  it('returns not_found for confirmed 404', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('Not Found', { status: 404 })),
    )

    const result = await getBundleDetailCached(validBundleId)
    expect(result.status).toBe('not_found')
  })

  it('returns not_found for non-UUID strings without calling backend', async () => {
    const fetchSpy = vi.fn()
    vi.stubGlobal('fetch', fetchSpy)

    const result = await getBundleDetailCached('invalid-uuid')
    expect(result.status).toBe('not_found')
    expect(fetchSpy).not.toHaveBeenCalled()
  })

  it('returns unavailable for transient upstream 500 failure instead of throwing or masking as 404', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('Backend internal error', { status: 500 })),
    )

    const result = await getBundleDetailCached(validBundleId)
    expect(result.status).toBe('unavailable')
  })
})
