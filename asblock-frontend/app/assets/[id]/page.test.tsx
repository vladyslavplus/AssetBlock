import { describe, expect, it, vi } from 'vitest'
import AssetDetailPage, { generateMetadata } from '@/app/assets/[id]/page'
import type { AssetDetailItemApi } from '@/lib/catalog/assets-api'
import type { AssetReview } from '@/lib/catalog/catalog-utils'
import * as assetDetailServer from '@/lib/server/asset-detail-server'
import * as paymentsCapabilities from '@/lib/server/payments-capabilities'

vi.mock('next/navigation', () => ({
  notFound: vi.fn(() => {
    throw new Error('NEXT_NOT_FOUND')
  }),
}))

vi.mock('@/components/site-header', () => ({
  SiteHeader: () => <div data-testid="site-header" />,
}))

vi.mock('@/components/site-footer', () => ({
  SiteFooter: () => <div data-testid="site-footer" />,
}))

vi.mock('@/components/assets/asset-detail-view', () => ({
  AssetDetailView: (props: unknown) => (
    <div data-testid="asset-detail-view" data-props={JSON.stringify(props)} />
  ),
}))

const dummyRaw = {
  id: '11111111-1111-4111-8111-111111111111',
  title: 'Fantasy Pack',
  description: 'Awesome fantasy assets',
  price: 25,
  categoryId: '22222222-2222-4222-8222-222222222222',
  category: '3D Models',
  sellerId: '33333333-3333-4333-8333-333333333333',
  sellerUsername: 'artist',
  status: 'ACTIVE',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  tags: ['fantasy', 'rpg'],
  rating: 4.5,
  ratingCount: 10,
  downloadsCount: 100,
  versions: [],
} as unknown as AssetDetailItemApi

describe('AssetDetailPage', () => {
  it('loads reviews and checkout capabilities in parallel after detail resolves', async () => {
    let reviewsStarted = false
    let capabilitiesStarted = false
    let resolveReviews!: (val: AssetReview[]) => void
    let resolveCapabilities!: (val: { checkoutConfigured: boolean }) => void

    const reviewsPromise = new Promise<AssetReview[]>((resolve) => {
      resolveReviews = resolve
    })
    const capabilitiesPromise = new Promise<{ checkoutConfigured: boolean }>((resolve) => {
      resolveCapabilities = resolve
    })

    const getDetailSpy = vi
      .spyOn(assetDetailServer, 'getAssetDetailCached')
      .mockResolvedValue(dummyRaw)
    const getReviewsSpy = vi
      .spyOn(assetDetailServer, 'getAssetReviewsCached')
      .mockImplementation(async () => {
        reviewsStarted = true
        return reviewsPromise
      })
    const getCapabilitiesSpy = vi
      .spyOn(paymentsCapabilities, 'fetchPaymentsCapabilitiesServer')
      .mockImplementation(async () => {
        capabilitiesStarted = true
        return capabilitiesPromise
      })

    const renderPromise = AssetDetailPage({
      params: Promise.resolve({ id: '11111111-1111-4111-8111-111111111111' }),
    })

    // Yield macro/microtasks so getAssetDetailCached resolves and parallel Promise.all starts
    await new Promise((r) => setTimeout(r, 0))

    // Both reads must be in-flight concurrently before either resolves
    expect(reviewsStarted).toBe(true)
    expect(capabilitiesStarted).toBe(true)

    // Resolve deferred reads
    resolveReviews([] as unknown as AssetReview[])
    resolveCapabilities({ checkoutConfigured: true })

    const result = await renderPromise

    expect(getDetailSpy).toHaveBeenCalledWith('11111111-1111-4111-8111-111111111111')
    expect(getReviewsSpy).toHaveBeenCalledWith('11111111-1111-4111-8111-111111111111')
    expect(getCapabilitiesSpy).toHaveBeenCalled()
    expect(result).toBeDefined()
  })

  it('triggers notFound when asset does not exist', async () => {
    vi.spyOn(assetDetailServer, 'getAssetDetailCached').mockResolvedValue(null)

    await expect(
      AssetDetailPage({
        params: Promise.resolve({ id: 'nonexistent-id' }),
      }),
    ).rejects.toThrow('NEXT_NOT_FOUND')
  })

  it('generates proper metadata', async () => {
    vi.spyOn(assetDetailServer, 'getAssetDetailCached').mockResolvedValue(dummyRaw)
    const meta = await generateMetadata({
      params: Promise.resolve({ id: '11111111-1111-4111-8111-111111111111' }),
    })
    expect(meta.title).toBe('Fantasy Pack · AssetBlock')
  })
})
