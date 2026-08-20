import {
  fetchAnalyticsAssetDetail,
  fetchAnalyticsBundleDetail,
  fetchAnalyticsCollections,
  fetchAnalyticsOverview,
  fetchAnalyticsProducts,
  fetchAnalyticsSalesPage,
} from '@/lib/analytics/analytics-api'
import type {
  AnalyticsCollectionsFilters,
  AnalyticsProductsFilters,
  AnalyticsSalesFilters,
  AnalyticsUtcRange,
} from '@/lib/analytics/analytics-types'

export const analyticsKeys = {
  all: ['seller', 'analytics'] as const,
  overview: (range: AnalyticsUtcRange) =>
    [...analyticsKeys.all, 'overview', range.from, range.to] as const,
  products: (range: AnalyticsUtcRange, filters: AnalyticsProductsFilters) =>
    [
      ...analyticsKeys.all,
      'products',
      range.from,
      range.to,
      filters.productType,
      filters.sort,
      filters.direction,
      filters.page,
      filters.pageSize,
    ] as const,
  sales: (range: AnalyticsUtcRange, filters: AnalyticsSalesFilters) =>
    [
      ...analyticsKeys.all,
      'sales',
      range.from,
      range.to,
      filters.productType,
      filters.pageSize,
    ] as const,
  collections: (range: AnalyticsUtcRange, filters: AnalyticsCollectionsFilters) =>
    [
      ...analyticsKeys.all,
      'collections',
      range.from,
      range.to,
      filters.sort,
      filters.direction,
      filters.page,
      filters.pageSize,
    ] as const,
  assetDetail: (assetId: string, range: AnalyticsUtcRange) =>
    [...analyticsKeys.all, 'asset', assetId, range.from, range.to] as const,
  bundleDetail: (bundleId: string, range: AnalyticsUtcRange) =>
    [...analyticsKeys.all, 'bundle', bundleId, range.from, range.to] as const,
}

export function sellerOverviewQueryOptions(range: AnalyticsUtcRange) {
  return {
    queryKey: analyticsKeys.overview(range),
    queryFn: ({ signal }: { signal: AbortSignal }) => fetchAnalyticsOverview(range, signal),
    staleTime: 120_000,
  }
}

export function sellerProductsQueryOptions(
  range: AnalyticsUtcRange,
  filters: AnalyticsProductsFilters,
) {
  return {
    queryKey: analyticsKeys.products(range, filters),
    queryFn: ({ signal }: { signal: AbortSignal }) =>
      fetchAnalyticsProducts(range, filters, signal),
    staleTime: 120_000,
  }
}

export function sellerSalesInfiniteQueryOptions(
  range: AnalyticsUtcRange,
  filters: AnalyticsSalesFilters,
) {
  return {
    queryKey: analyticsKeys.sales(range, filters),
    queryFn: ({ pageParam, signal }: { pageParam: string | undefined; signal: AbortSignal }) =>
      fetchAnalyticsSalesPage(range, filters, pageParam, signal),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (lastPage: Awaited<ReturnType<typeof fetchAnalyticsSalesPage>>) =>
      lastPage.hasMore ? (lastPage.nextCursor ?? undefined) : undefined,
    staleTime: 30_000,
  }
}

export function sellerCollectionsQueryOptions(
  range: AnalyticsUtcRange,
  filters: AnalyticsCollectionsFilters,
) {
  return {
    queryKey: analyticsKeys.collections(range, filters),
    queryFn: ({ signal }: { signal: AbortSignal }) =>
      fetchAnalyticsCollections(range, filters, signal),
    staleTime: 120_000,
  }
}

export function sellerAssetDetailQueryOptions(assetId: string, range: AnalyticsUtcRange) {
  return {
    queryKey: analyticsKeys.assetDetail(assetId, range),
    queryFn: ({ signal }: { signal: AbortSignal }) =>
      fetchAnalyticsAssetDetail(assetId, range, signal),
    staleTime: 120_000,
  }
}

export function sellerBundleDetailQueryOptions(bundleId: string, range: AnalyticsUtcRange) {
  return {
    queryKey: analyticsKeys.bundleDetail(bundleId, range),
    queryFn: ({ signal }: { signal: AbortSignal }) =>
      fetchAnalyticsBundleDetail(bundleId, range, signal),
    staleTime: 120_000,
  }
}
