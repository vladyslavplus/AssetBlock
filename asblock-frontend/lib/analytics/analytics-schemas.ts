import { z } from 'zod'

import { parseDateOnlyUtc } from '@/lib/analytics/analytics-format'
import {
  ANALYTICS_COLLECTION_SORTS,
  ANALYTICS_COLLECTION_STATUSES,
  ANALYTICS_GRANULARITIES,
  ANALYTICS_PRODUCT_AVAILABILITY,
  ANALYTICS_PRODUCT_KINDS,
  ANALYTICS_PRODUCT_SORTS,
  ANALYTICS_PRODUCT_TYPE_FILTERS,
  ANALYTICS_SORT_DIRECTIONS,
  ANALYTICS_TRAFFIC_SOURCES,
} from '@/lib/analytics/analytics-types'

const dateOnlySchema = z.string().refine((value) => parseDateOnlyUtc(value) != null, {
  message: 'Invalid UTC date (YYYY-MM-DD)',
})

const isoTimestampSchema = z.string().datetime({ offset: true })

const moneyCentsMetricSchema = z
  .object({
    current: z.number().int().safe().nonnegative(),
    previous: z.number().int().safe().nonnegative(),
    absoluteChange: z.number().int().safe(),
    percentageChange: z.number().nullable(),
  })
  .strict()

const countMetricSchema = z
  .object({
    current: z.number().int().safe().nonnegative(),
    previous: z.number().int().safe().nonnegative(),
    absoluteChange: z.number().int().safe(),
    percentageChange: z.number().nullable(),
  })
  .strict()

const rateMetricSchema = z
  .object({
    current: z.number().min(0).max(1).nullable(),
    previous: z.number().min(0).max(1).nullable(),
    absoluteChange: z.number().min(-1).max(1).nullable(),
    percentageChange: z.number().nullable(),
  })
  .strict()

export const analyticsSeriesPointSchema = z
  .object({
    bucketStart: isoTimestampSchema,
    grossRevenueCents: z.number().int().safe().nonnegative(),
    orders: z.number().int().safe().nonnegative(),
    unitsSold: z.number().int().safe().nonnegative(),
    productViews: z.number().int().safe().nonnegative().nullable(),
    uniqueVisitors: z.number().int().safe().nonnegative().nullable(),
    checkoutStarts: z.number().int().safe().nonnegative(),
    completedOrders: z.number().int().safe().nonnegative(),
    downloadRequests: z.number().int().safe().nonnegative().nullable(),
  })
  .strict()

const engagementCountMetricSchema = z
  .object({
    current: z.number().int().safe().nonnegative(),
    previous: z.number().int().safe().nonnegative().nullable(),
    absoluteChange: z.number().int().safe().nullable(),
    percentageChange: z.number().nullable(),
  })
  .strict()

export const analyticsEngagementTotalsSchema = z
  .object({
    productViews: engagementCountMetricSchema,
    uniqueVisitors: engagementCountMetricSchema,
    downloadRequests: engagementCountMetricSchema,
    collectionViews: engagementCountMetricSchema,
    collectionItemClicks: engagementCountMetricSchema,
  })
  .strict()

export const analyticsCommerceFunnelSchema = z
  .object({
    checkoutStarts: z.number().int().safe().nonnegative(),
    stripeSessionsAttached: z.number().int().safe().nonnegative(),
    completedOrders: z.number().int().safe().nonnegative(),
    cancelledCheckouts: z.number().int().safe().nonnegative(),
    pendingCheckouts: z.number().int().safe().nonnegative(),
    checkoutCompletionRate: z.number().min(0).max(1).nullable(),
    terminalAbandonmentRate: z.number().min(0).max(1).nullable(),
  })
  .strict()

export const analyticsTrackedFunnelSchema = z
  .object({
    viewSessions: z.number().int().safe().nonnegative(),
    checkoutSessions: z.number().int().safe().nonnegative(),
    completedSessions: z.number().int().safe().nonnegative(),
    viewToCheckoutRate: z.number().min(0).max(1).nullable(),
    checkoutToCompletedRate: z.number().min(0).max(1).nullable(),
    viewToCompletedRate: z.number().min(0).max(1).nullable(),
  })
  .strict()

export const analyticsExternalReferrerRowSchema = z
  .object({
    referrerHost: z.string(),
    productViews: z.number().int().safe().nonnegative(),
    uniqueVisitors: z.number().int().safe().nonnegative(),
    checkoutStarts: z.number().int().safe().nonnegative(),
    completedOrders: z.number().int().safe().nonnegative(),
    attributedGrossRevenueCents: z.number().int().safe().nonnegative(),
  })
  .strict()

export const analyticsTrafficSourceRowSchema = z
  .object({
    source: z.enum(ANALYTICS_TRAFFIC_SOURCES),
    productViews: z.number().int().safe().nonnegative(),
    uniqueVisitors: z.number().int().safe().nonnegative(),
    checkoutStarts: z.number().int().safe().nonnegative(),
    completedOrders: z.number().int().safe().nonnegative(),
    attributedGrossRevenueCents: z.number().int().safe().nonnegative(),
    externalReferrers: z.array(analyticsExternalReferrerRowSchema).nullable(),
  })
  .strict()

export const analyticsProductItemSchema = z
  .object({
    productKind: z.enum(ANALYTICS_PRODUCT_KINDS),
    productId: z.string().uuid(),
    title: z.string(),
    availability: z.enum(ANALYTICS_PRODUCT_AVAILABILITY),
    grossRevenueCents: z.number().int().safe().nonnegative(),
    directRevenueCents: z.number().int().safe().nonnegative().nullable(),
    bundleAllocatedRevenueCents: z.number().int().safe().nonnegative().nullable(),
    orders: z.number().int().safe().nonnegative(),
    unitsSold: z.number().int().safe().nonnegative(),
    averageRating: z.number().min(0).max(5).nullable(),
    reviewCount: z.number().int().safe().nonnegative().nullable(),
    latestSaleAt: isoTimestampSchema.nullable(),
    currentPriceCents: z.number().int().safe().nonnegative().nullable(),
    listPriceCents: z.number().int().safe().nonnegative().nullable(),
    discountPercent: z.number().min(0).max(100).nullable(),
  })
  .strict()

export const sellerAnalyticsOverviewSchema = z
  .object({
    from: dateOnlySchema,
    to: dateOnlySchema,
    comparisonFrom: dateOnlySchema,
    comparisonTo: dateOnlySchema,
    timezone: z.literal('UTC'),
    granularity: z.enum(ANALYTICS_GRANULARITIES),
    generatedAt: isoTimestampSchema,
    currency: z.literal('usd'),
    engagementAvailableFrom: isoTimestampSchema.nullable(),
    grossRevenue: moneyCentsMetricSchema,
    directRevenue: moneyCentsMetricSchema,
    bundleRevenue: moneyCentsMetricSchema,
    orders: countMetricSchema,
    unitsSold: countMetricSchema,
    averageOrderValue: moneyCentsMetricSchema,
    uniqueCustomers: countMetricSchema,
    newCustomers: countMetricSchema,
    returningCustomers: countMetricSchema,
    repeatCustomers: countMetricSchema,
    repeatCustomerRate: rateMetricSchema,
    averageRating: z.number().min(0).max(5).nullable(),
    newReviews: countMetricSchema,
    series: z.array(analyticsSeriesPointSchema),
    topAssets: z.array(analyticsProductItemSchema),
    topBundles: z.array(analyticsProductItemSchema),
    engagementTotals: analyticsEngagementTotalsSchema.nullable(),
    commerceFunnel: analyticsCommerceFunnelSchema.nullable(),
    trackedFunnel: analyticsTrackedFunnelSchema.nullable(),
    trackedCheckoutCoverage: z.number().min(0).max(1).nullable(),
    trafficSources: z.array(analyticsTrafficSourceRowSchema).nullable(),
  })
  .strict()

export const analyticsProductsResultSchema = z
  .object({
    from: dateOnlySchema,
    to: dateOnlySchema,
    timezone: z.literal('UTC'),
    currency: z.literal('usd'),
    generatedAt: isoTimestampSchema,
    items: z.array(analyticsProductItemSchema),
    totalCount: z.number().int().safe().nonnegative(),
    page: z.number().int().safe().positive(),
    pageSize: z.number().int().safe().positive(),
  })
  .strict()

export const analyticsSaleItemSchema = z
  .object({
    productKind: z.enum(ANALYTICS_PRODUCT_KINDS),
    productId: z.string().uuid(),
    productTitle: z.string(),
    orderId: z.string().uuid(),
    purchasedAt: isoTimestampSchema,
    units: z.number().int().safe().positive(),
    grossRevenueCents: z.number().int().safe().nonnegative(),
  })
  .strict()

export const analyticsSalesResultSchema = z
  .object({
    from: dateOnlySchema,
    to: dateOnlySchema,
    timezone: z.literal('UTC'),
    currency: z.literal('usd'),
    generatedAt: isoTimestampSchema,
    items: z.array(analyticsSaleItemSchema),
    hasMore: z.boolean(),
    nextCursor: z.string().nullable(),
  })
  .strict()

export const analyticsProductTypeFilterSchema = z.enum(ANALYTICS_PRODUCT_TYPE_FILTERS)
export const analyticsProductSortSchema = z.enum(ANALYTICS_PRODUCT_SORTS)
export const analyticsCollectionSortSchema = z.enum(ANALYTICS_COLLECTION_SORTS)
export const analyticsSortDirectionSchema = z.enum(ANALYTICS_SORT_DIRECTIONS)

export const analyticsCollectionTopAssetSchema = z
  .object({
    assetId: z.string().uuid(),
    title: z.string(),
    clicks: z.number().int().safe().nonnegative(),
  })
  .strict()

export const analyticsCollectionItemSchema = z
  .object({
    collectionId: z.string().uuid(),
    title: z.string(),
    status: z.enum(ANALYTICS_COLLECTION_STATUSES),
    views: z.number().int().safe().nonnegative().nullable(),
    uniqueVisitors: z.number().int().safe().nonnegative().nullable(),
    itemClicks: z.number().int().safe().nonnegative().nullable(),
    clickThroughRate: z.number().min(0).max(1).nullable(),
    attributedCheckoutStarts: z.number().int().safe().nonnegative(),
    attributedCompletedOrders: z.number().int().safe().nonnegative(),
    attributedGrossRevenueCents: z.number().int().safe().nonnegative(),
    topClickedAssets: z.array(analyticsCollectionTopAssetSchema).nullable(),
  })
  .strict()

export const analyticsCollectionsResultSchema = z
  .object({
    from: dateOnlySchema,
    to: dateOnlySchema,
    timezone: z.literal('UTC'),
    currency: z.literal('usd'),
    generatedAt: isoTimestampSchema,
    engagementAvailableFrom: isoTimestampSchema.nullable(),
    items: z.array(analyticsCollectionItemSchema),
    totalCount: z.number().int().safe().nonnegative(),
    page: z.number().int().safe().positive(),
    pageSize: z.number().int().safe().positive(),
  })
  .strict()

export const analyticsProductDetailAssetSchema = z
  .object({
    from: dateOnlySchema,
    to: dateOnlySchema,
    timezone: z.literal('UTC'),
    generatedAt: isoTimestampSchema,
    currency: z.literal('usd'),
    granularity: z.enum(ANALYTICS_GRANULARITIES),
    engagementAvailableFrom: isoTimestampSchema.nullable(),
    assetId: z.string().uuid(),
    title: z.string(),
    availability: z.enum(ANALYTICS_PRODUCT_AVAILABILITY),
    grossRevenueCents: z.number().int().safe().nonnegative(),
    directRevenueCents: z.number().int().safe().nonnegative(),
    bundleAllocatedRevenueCents: z.number().int().safe().nonnegative(),
    orders: z.number().int().safe().nonnegative(),
    unitsSold: z.number().int().safe().nonnegative(),
    averageRating: z.number().min(0).max(5).nullable(),
    reviewCount: z.number().int().safe().nonnegative(),
    latestSaleAt: isoTimestampSchema.nullable(),
    checkoutStarts: z.number().int().safe().nonnegative(),
    productViews: z.number().int().safe().nonnegative().nullable(),
    uniqueVisitors: z.number().int().safe().nonnegative().nullable(),
    downloadRequests: z.number().int().safe().nonnegative().nullable(),
    trackedViewToCheckoutRate: z.number().min(0).max(1).nullable(),
    checkoutCompletionRate: z.number().min(0).max(1).nullable(),
    series: z.array(analyticsSeriesPointSchema),
  })
  .strict()

export const analyticsProductDetailBundleSchema = z
  .object({
    from: dateOnlySchema,
    to: dateOnlySchema,
    timezone: z.literal('UTC'),
    generatedAt: isoTimestampSchema,
    currency: z.literal('usd'),
    granularity: z.enum(ANALYTICS_GRANULARITIES),
    engagementAvailableFrom: isoTimestampSchema.nullable(),
    bundleId: z.string().uuid(),
    title: z.string(),
    availability: z.enum(ANALYTICS_PRODUCT_AVAILABILITY),
    grossRevenueCents: z.number().int().safe().nonnegative(),
    orders: z.number().int().safe().nonnegative(),
    unitsSold: z.number().int().safe().nonnegative(),
    currentPriceCents: z.number().int().safe().nonnegative().nullable(),
    listPriceCents: z.number().int().safe().nonnegative().nullable(),
    discountPercent: z.number().min(0).max(100).nullable(),
    latestSaleAt: isoTimestampSchema.nullable(),
    checkoutStarts: z.number().int().safe().nonnegative(),
    productViews: z.number().int().safe().nonnegative().nullable(),
    uniqueVisitors: z.number().int().safe().nonnegative().nullable(),
    trackedViewToCheckoutRate: z.number().min(0).max(1).nullable(),
    checkoutCompletionRate: z.number().min(0).max(1).nullable(),
    series: z.array(analyticsSeriesPointSchema),
  })
  .strict()
