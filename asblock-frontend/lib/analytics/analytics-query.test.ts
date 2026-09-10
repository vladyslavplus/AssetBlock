import { beforeEach, describe, expect, it, vi } from 'vitest'

import {
  sellerCollectionsQueryOptions,
  sellerOverviewQueryOptions,
  sellerProductsQueryOptions,
  sellerSalesInfiniteQueryOptions,
} from '@/lib/analytics/analytics-query'

const api = vi.hoisted(() => ({
  fetchAnalyticsAssetDetail: vi.fn(),
  fetchAnalyticsBundleDetail: vi.fn(),
  fetchAnalyticsCollections: vi.fn(),
  fetchAnalyticsOverview: vi.fn(),
  fetchAnalyticsProducts: vi.fn(),
  fetchAnalyticsSalesPage: vi.fn(),
}))

vi.mock('@/lib/analytics/analytics-api', () => api)

const range = { from: '2026-08-07', to: '2026-09-06' }
const productFilters = {
  productType: 'ALL' as const,
  sort: 'REVENUE' as const,
  direction: 'DESC' as const,
  page: 1,
  pageSize: 20,
}
const collectionFilters = {
  sort: 'VIEWS' as const,
  direction: 'DESC' as const,
  page: 1,
  pageSize: 20,
}
const salesFilters = { productType: 'ALL' as const, pageSize: 25 }

describe('analytics query options', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    api.fetchAnalyticsOverview.mockResolvedValue({})
    api.fetchAnalyticsProducts.mockResolvedValue({})
    api.fetchAnalyticsCollections.mockResolvedValue({})
    api.fetchAnalyticsSalesPage.mockResolvedValue({ hasMore: false })
  })

  it('does not forward React Query cancellation signals into regular reads', async () => {
    const controller = new AbortController()
    const context = { pageParam: undefined, signal: controller.signal }

    await sellerOverviewQueryOptions(range).queryFn()
    await sellerProductsQueryOptions(range, productFilters).queryFn()
    await sellerCollectionsQueryOptions(range, collectionFilters).queryFn()
    await sellerSalesInfiniteQueryOptions(range, salesFilters).queryFn(context)

    expect(api.fetchAnalyticsOverview).toHaveBeenCalledWith(range)
    expect(api.fetchAnalyticsProducts).toHaveBeenCalledWith(range, productFilters)
    expect(api.fetchAnalyticsCollections).toHaveBeenCalledWith(range, collectionFilters)
    expect(api.fetchAnalyticsSalesPage).toHaveBeenCalledWith(range, salesFilters, undefined)
  })
})
