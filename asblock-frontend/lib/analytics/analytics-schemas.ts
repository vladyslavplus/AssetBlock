import { z } from 'zod'

import { parseDateOnlyUtc } from '@/lib/analytics/analytics-format'
import {
  ANALYTICS_GRANULARITIES,
  ANALYTICS_PRODUCT_AVAILABILITY,
  ANALYTICS_PRODUCT_KINDS,
  ANALYTICS_PRODUCT_SORTS,
  ANALYTICS_PRODUCT_TYPE_FILTERS,
  ANALYTICS_SORT_DIRECTIONS,
} from '@/lib/analytics/analytics-types'

const dateOnlySchema = z
  .string()
  .refine((value) => parseDateOnlyUtc(value) != null, {
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
export const analyticsSortDirectionSchema = z.enum(ANALYTICS_SORT_DIRECTIONS)
