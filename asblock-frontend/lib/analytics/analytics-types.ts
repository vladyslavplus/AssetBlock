import type { z } from 'zod'

import type {
  analyticsCollectionItemSchema,
  analyticsCollectionsResultSchema,
  analyticsProductDetailAssetSchema,
  analyticsProductDetailBundleSchema,
  analyticsProductItemSchema,
  analyticsProductsResultSchema,
  analyticsSaleItemSchema,
  analyticsSalesResultSchema,
  analyticsSeriesPointSchema,
  sellerAnalyticsOverviewSchema,
} from '@/lib/analytics/analytics-schemas'

export const ANALYTICS_TRAFFIC_SOURCES = [
  'CATALOG',
  'SEARCH',
  'SELLER_PROFILE',
  'COLLECTION',
  'BUNDLE_PAGE',
  'DIRECT_INTERNAL',
  'EXTERNAL',
  'UNKNOWN',
] as const
export type AnalyticsTrafficSource = (typeof ANALYTICS_TRAFFIC_SOURCES)[number]

export const ANALYTICS_RANGE_PRESETS = ['7d', '30d', '90d', 'ytd', 'custom'] as const
export type AnalyticsRangePreset = (typeof ANALYTICS_RANGE_PRESETS)[number]

export const ANALYTICS_PRODUCT_TYPE_FILTERS = ['ALL', 'ASSET', 'BUNDLE'] as const
export type AnalyticsProductTypeFilter = (typeof ANALYTICS_PRODUCT_TYPE_FILTERS)[number]

export const ANALYTICS_PRODUCT_SORTS = ['REVENUE', 'ORDERS', 'UNITS', 'RATING', 'RECENT'] as const
export type AnalyticsProductSort = (typeof ANALYTICS_PRODUCT_SORTS)[number]

export const ANALYTICS_COLLECTION_SORTS = [
  'VIEWS',
  'CLICKS',
  'ATTRIBUTED_REVENUE',
  'RECENT',
] as const
export type AnalyticsCollectionSort = (typeof ANALYTICS_COLLECTION_SORTS)[number]

export const ANALYTICS_COLLECTION_STATUSES = ['DRAFT', 'PUBLISHED', 'ARCHIVED'] as const
export type AnalyticsCollectionStatus = (typeof ANALYTICS_COLLECTION_STATUSES)[number]

export const ANALYTICS_SORT_DIRECTIONS = ['ASC', 'DESC'] as const
export type AnalyticsSortDirection = (typeof ANALYTICS_SORT_DIRECTIONS)[number]

export const ANALYTICS_GRANULARITIES = ['DAY', 'WEEK', 'MONTH'] as const
export type AnalyticsGranularity = (typeof ANALYTICS_GRANULARITIES)[number]

export const ANALYTICS_PRODUCT_KINDS = ['ASSET', 'BUNDLE'] as const
export type AnalyticsProductKind = (typeof ANALYTICS_PRODUCT_KINDS)[number]

export const ANALYTICS_PRODUCT_AVAILABILITY = ['ACTIVE', 'UNAVAILABLE', 'ARCHIVED'] as const
export type AnalyticsProductAvailability = (typeof ANALYTICS_PRODUCT_AVAILABILITY)[number]

export type AnalyticsSeriesMetric = 'revenue' | 'orders' | 'units'

export const ANALYTICS_DEFAULT_PRODUCTS_PAGE_SIZE = 20
export const ANALYTICS_DEFAULT_COLLECTIONS_PAGE_SIZE = 20
export const ANALYTICS_DEFAULT_SALES_PAGE_SIZE = 25
export const ANALYTICS_MAX_DAYS = 366
export const ANALYTICS_MAX_PAGE_SIZE = 100
export const ANALYTICS_MAX_PRODUCTS_PAGE = 10_000
export const ANALYTICS_MAX_PRODUCTS_OFFSET = 100_000
export const ANALYTICS_MAX_CURSOR_LENGTH = 256

export interface MoneyCentsMetric {
  current: number
  previous: number
  absoluteChange: number
  percentageChange: number | null
}

export interface CountMetric {
  current: number
  previous: number
  absoluteChange: number
  percentageChange: number | null
}

export interface RateMetric {
  current: number | null
  previous: number | null
  absoluteChange: number | null
  percentageChange: number | null
}

export type AnalyticsSeriesPoint = z.infer<typeof analyticsSeriesPointSchema>
export type AnalyticsProductItem = z.infer<typeof analyticsProductItemSchema>
export type SellerAnalyticsOverview = z.infer<typeof sellerAnalyticsOverviewSchema>
export type AnalyticsProductsResult = z.infer<typeof analyticsProductsResultSchema>
export type AnalyticsSaleItem = z.infer<typeof analyticsSaleItemSchema>
export type AnalyticsSalesResult = z.infer<typeof analyticsSalesResultSchema>
export type AnalyticsCollectionItem = z.infer<typeof analyticsCollectionItemSchema>
export type AnalyticsCollectionsResult = z.infer<typeof analyticsCollectionsResultSchema>
export type AnalyticsAssetDetail = z.infer<typeof analyticsProductDetailAssetSchema>
export type AnalyticsBundleDetail = z.infer<typeof analyticsProductDetailBundleSchema>

export interface AnalyticsUtcRange {
  from: string
  to: string
}

export interface AnalyticsProductsFilters {
  productType: AnalyticsProductTypeFilter
  sort: AnalyticsProductSort
  direction: AnalyticsSortDirection
  page: number
  pageSize: number
}

export interface AnalyticsSalesFilters {
  productType: AnalyticsProductTypeFilter
  pageSize: number
}

export interface AnalyticsCollectionsFilters {
  sort: AnalyticsCollectionSort
  direction: AnalyticsSortDirection
  page: number
  pageSize: number
}

export interface AnalyticsUrlState {
  range: AnalyticsRangePreset
  customFrom: string | null
  customTo: string | null
  productType: AnalyticsProductTypeFilter
  sort: AnalyticsProductSort
  direction: AnalyticsSortDirection
  page: number
  collectionSort: AnalyticsCollectionSort
  collectionDirection: AnalyticsSortDirection
  collectionPage: number
}
