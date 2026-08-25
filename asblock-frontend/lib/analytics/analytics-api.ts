import type { z } from 'zod'

import {
  analyticsCollectionsResultSchema,
  analyticsProductsResultSchema,
  analyticsProductDetailAssetSchema,
  analyticsProductDetailBundleSchema,
  analyticsSalesResultSchema,
  sellerAnalyticsOverviewSchema,
} from '@/lib/analytics/analytics-schemas'
import type {
  AnalyticsAssetDetail,
  AnalyticsBundleDetail,
  AnalyticsCollectionsFilters,
  AnalyticsCollectionsResult,
  AnalyticsProductsFilters,
  AnalyticsProductsResult,
  AnalyticsSalesFilters,
  AnalyticsSalesResult,
  AnalyticsUtcRange,
  SellerAnalyticsOverview,
} from '@/lib/analytics/analytics-types'
import { ApiRequestError } from '@/lib/http/api-client'
import { fetchBffJson } from '@/lib/http/bff-json'
import { isAbortError, toAbortError } from '@/lib/http/is-abort-error'

function buildRangeQuery(range: AnalyticsUtcRange): string {
  const params = new URLSearchParams({
    from: range.from,
    to: range.to,
  })
  return params.toString()
}

function throwBffFailure(result: {
  ok: false
  status: number
  message: string
  body?: unknown
}): never {
  if (result.status === 401) {
    throw new ApiRequestError('Please sign in to view analytics.', 401, result.body)
  }
  throw new ApiRequestError(result.message, result.status, result.body)
}

async function fetchAnalyticsJson<TSchema extends z.ZodTypeAny>(
  path: string,
  schema: TSchema,
  signal?: AbortSignal,
): Promise<z.infer<TSchema>> {
  let result: Awaited<ReturnType<typeof fetchBffJson<TSchema>>>
  try {
    result = await fetchBffJson(path, schema, { signal })
  } catch (error) {
    // Keep AbortError as cancellation; do not map it to a 502 ApiRequestError.
    if (isAbortError(error, signal)) throw toAbortError(error, signal)
    throw new ApiRequestError('Unexpected analytics response', 502, null)
  }

  if (!result.ok) {
    throwBffFailure(result)
  }

  return result.data
}

export async function fetchAnalyticsOverview(
  range: AnalyticsUtcRange,
  signal?: AbortSignal,
): Promise<SellerAnalyticsOverview> {
  const qs = buildRangeQuery(range)
  return fetchAnalyticsJson(
    `/api/seller/analytics/overview?${qs}`,
    sellerAnalyticsOverviewSchema,
    signal,
  )
}

export async function fetchAnalyticsProducts(
  range: AnalyticsUtcRange,
  filters: AnalyticsProductsFilters,
  signal?: AbortSignal,
): Promise<AnalyticsProductsResult> {
  const params = new URLSearchParams({
    from: range.from,
    to: range.to,
    productType: filters.productType,
    sort: filters.sort,
    direction: filters.direction,
    page: String(filters.page),
    pageSize: String(filters.pageSize),
  })
  return fetchAnalyticsJson(
    `/api/seller/analytics/products?${params.toString()}`,
    analyticsProductsResultSchema,
    signal,
  )
}

export async function fetchAnalyticsSalesPage(
  range: AnalyticsUtcRange,
  filters: AnalyticsSalesFilters,
  cursor?: string,
  signal?: AbortSignal,
): Promise<AnalyticsSalesResult> {
  const params = new URLSearchParams({
    from: range.from,
    to: range.to,
    productType: filters.productType,
    pageSize: String(filters.pageSize),
  })
  if (cursor) {
    params.set('cursor', cursor)
  }
  return fetchAnalyticsJson(
    `/api/seller/analytics/sales?${params.toString()}`,
    analyticsSalesResultSchema,
    signal,
  )
}

export async function fetchAnalyticsCollections(
  range: AnalyticsUtcRange,
  filters: AnalyticsCollectionsFilters,
  signal?: AbortSignal,
): Promise<AnalyticsCollectionsResult> {
  const params = new URLSearchParams({
    from: range.from,
    to: range.to,
    sort: filters.sort,
    direction: filters.direction,
    page: String(filters.page),
    pageSize: String(filters.pageSize),
  })
  return fetchAnalyticsJson(
    `/api/seller/analytics/collections?${params.toString()}`,
    analyticsCollectionsResultSchema,
    signal,
  )
}

export async function fetchAnalyticsAssetDetail(
  assetId: string,
  range: AnalyticsUtcRange,
  signal?: AbortSignal,
): Promise<AnalyticsAssetDetail> {
  const qs = buildRangeQuery(range)
  return fetchAnalyticsJson(
    `/api/seller/analytics/products/assets/${encodeURIComponent(assetId)}?${qs}`,
    analyticsProductDetailAssetSchema,
    signal,
  )
}

export async function fetchAnalyticsBundleDetail(
  bundleId: string,
  range: AnalyticsUtcRange,
  signal?: AbortSignal,
): Promise<AnalyticsBundleDetail> {
  const qs = buildRangeQuery(range)
  return fetchAnalyticsJson(
    `/api/seller/analytics/products/bundles/${encodeURIComponent(bundleId)}?${qs}`,
    analyticsProductDetailBundleSchema,
    signal,
  )
}
