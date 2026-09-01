import { describe, expect, it, vi, beforeEach } from 'vitest'
import BundleDetailPage, { generateMetadata } from '@/app/bundles/[id]/page'
import * as bundleServer from '@/lib/server/bundle-detail-server'
import * as paymentsCapabilitiesServer from '@/lib/server/payments-capabilities'
import type { BundleDetail } from '@/lib/bundles/bundle-types'

const notFoundMock = vi.fn()
vi.mock('next/navigation', () => ({
  notFound: () => notFoundMock(),
}))

vi.mock('@/components/bundles/bundle-detail-view', () => ({
  BundleDetailView: (props: unknown) => (
    <div data-testid="bundle-detail-view" data-props={JSON.stringify(props)} />
  ),
}))

vi.mock('@/components/site-header', () => ({ SiteHeader: () => <header /> }))
vi.mock('@/components/site-footer', () => ({ SiteFooter: () => <footer /> }))

describe('BundleDetailPage Server Component & Metadata', () => {
  const validBundleId = '11111111-1111-4111-8111-111111111111'
  const sampleBundle: BundleDetail = {
    id: validBundleId,
    revisionId: '22222222-2222-4222-8222-222222222222',
    revisionNumber: 1,
    title: 'Cyberpunk Asset Kit',
    description: 'Neon assets for your sci-fi game.',
    price: 39.99,
    listPriceTotal: 79.99,
    savingsAmount: 40.0,
    savingsPercent: 50,
    currency: 'USD',
    sellerId: '33333333-3333-4333-8333-333333333333',
    sellerUsername: 'neon_artist',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: null,
    archivedAt: null,
    isArchived: false,
    isAvailable: true,
    items: [],
  }

  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('generates dynamic metadata from cached bundle detail', async () => {
    vi.spyOn(bundleServer, 'getBundleDetailCached').mockResolvedValue({
      status: 'success',
      bundle: sampleBundle,
    })

    const meta = await generateMetadata({ params: Promise.resolve({ id: validBundleId }) })
    expect(meta.title).toBe('Cyberpunk Asset Kit · AssetBlock')
    expect(meta.description).toContain('Neon assets')
  })

  it('generates not found metadata for invalid UUID or missing bundle', async () => {
    vi.spyOn(bundleServer, 'getBundleDetailCached').mockResolvedValue({ status: 'not_found' })

    const metaInvalid = await generateMetadata({ params: Promise.resolve({ id: 'invalid-id' }) })
    expect(metaInvalid.title).toBe('Bundle Not Found · AssetBlock')

    const metaMissing = await generateMetadata({ params: Promise.resolve({ id: validBundleId }) })
    expect(metaMissing.title).toBe('Bundle Not Found · AssetBlock')
  })

  it('loads bundle detail and payments capabilities in parallel', async () => {
    let bundleStarted = false
    let capabilitiesStarted = false
    let resolveBundle!: (val: bundleServer.BundleServerResult) => void
    let resolveCapabilities!: (val: { checkoutConfigured: boolean }) => void

    const bundlePromise = new Promise<bundleServer.BundleServerResult>((resolve) => {
      resolveBundle = resolve
    })
    const capabilitiesPromise = new Promise<{ checkoutConfigured: boolean }>((resolve) => {
      resolveCapabilities = resolve
    })

    const _getBundleSpy = vi
      .spyOn(bundleServer, 'getBundleDetailCached')
      .mockImplementation(async () => {
        bundleStarted = true
        return bundlePromise
      })

    const _getCapabilitiesSpy = vi
      .spyOn(paymentsCapabilitiesServer, 'fetchPaymentsCapabilitiesServer')
      .mockImplementation(async () => {
        capabilitiesStarted = true
        return capabilitiesPromise
      })

    const pageExecution = BundleDetailPage({
      params: Promise.resolve({ id: validBundleId }),
    })

    // Allow microtask to resolve params
    await Promise.resolve()

    // Assert parallel execution before either resolves
    expect(bundleStarted).toBe(true)
    expect(capabilitiesStarted).toBe(true)

    resolveBundle({ status: 'success', bundle: sampleBundle })
    resolveCapabilities({ checkoutConfigured: true })

    const element = await pageExecution
    expect(element).toBeDefined()
  })

  it('triggers notFound when bundle does not exist (confirmed 404)', async () => {
    vi.spyOn(bundleServer, 'getBundleDetailCached').mockResolvedValue({ status: 'not_found' })
    vi.spyOn(paymentsCapabilitiesServer, 'fetchPaymentsCapabilitiesServer').mockResolvedValue({
      checkoutConfigured: true,
    })

    await BundleDetailPage({ params: Promise.resolve({ id: validBundleId }) })
    expect(notFoundMock).toHaveBeenCalled()
  })

  it('does not trigger notFound on transient 500/timeout error and renders view with undefined initialBundle', async () => {
    vi.spyOn(bundleServer, 'getBundleDetailCached').mockResolvedValue({
      status: 'unavailable',
      error: 'Status 500',
    })
    vi.spyOn(paymentsCapabilitiesServer, 'fetchPaymentsCapabilitiesServer').mockResolvedValue({
      checkoutConfigured: true,
    })

    const element = await BundleDetailPage({ params: Promise.resolve({ id: validBundleId }) })
    expect(notFoundMock).not.toHaveBeenCalled()
    expect(element).toBeDefined()
  })
})
