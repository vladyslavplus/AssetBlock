import {
  fetchAnalyticsOverview,
  fetchAnalyticsProducts,
  fetchAnalyticsSalesPage,
} from '@/lib/analytics/analytics-api'
import type {
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
