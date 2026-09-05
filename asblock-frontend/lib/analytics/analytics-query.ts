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

/**
 * Query reads intentionally do not consume React Query's cancellation signal.
 * A transient dev remount or route replacement can abort that signal without a
 * reason; allowing these short idempotent reads to finish avoids unhandled
 * browser abort rejections and warms the query cache for the next observer.
 */
export function sellerOverviewQueryOptions(range: AnalyticsUtcRange) {
  return {
    queryKey: analyticsKeys.overview(range),
    queryFn: () => fetchAnalyticsOverview(range),
    staleTime: 120_000,
  }
}

export function sellerProductsQueryOptions(
  range: AnalyticsUtcRange,
  filters: AnalyticsProductsFilters,
) {
  return {
    queryKey: analyticsKeys.products(range, filters),
    queryFn: () => fetchAnalyticsProducts(range, filters),
    staleTime: 120_000,
  }
}

export function sellerSalesInfiniteQueryOptions(
  range: AnalyticsUtcRange,
  filters: AnalyticsSalesFilters,
) {
  return {
    queryKey: analyticsKeys.sales(range, filters),
    queryFn: ({ pageParam }: { pageParam: string | undefined }) =>
      fetchAnalyticsSalesPage(range, filters, pageParam),
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
    queryFn: () => fetchAnalyticsCollections(range, filters),
    staleTime: 120_000,
  }
}

export function sellerAssetDetailQueryOptions(assetId: string, range: AnalyticsUtcRange) {
  return {
    queryKey: analyticsKeys.assetDetail(assetId, range),
    queryFn: () => fetchAnalyticsAssetDetail(assetId, range),
    staleTime: 120_000,
  }
}

export function sellerBundleDetailQueryOptions(bundleId: string, range: AnalyticsUtcRange) {
  return {
    queryKey: analyticsKeys.bundleDetail(bundleId, range),
    queryFn: () => fetchAnalyticsBundleDetail(bundleId, range),
    staleTime: 120_000,
  }
}
