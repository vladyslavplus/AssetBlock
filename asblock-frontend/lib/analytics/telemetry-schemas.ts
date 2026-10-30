import { z } from 'zod'

import {
  ANALYTICS_DEVICE_CLASSES,
  ANALYTICS_EVENT_TYPES,
  ANALYTICS_TRAFFIC_SOURCES,
} from '@/lib/analytics/telemetry-constants'
import { ANALYTICS_REFERRER_HOST_MAX_LENGTH } from '@/lib/analytics/analytics-referrer'

export const analyticsEventTypeSchema = z.enum(ANALYTICS_EVENT_TYPES)
export const analyticsTrafficSourceSchema = z.enum(ANALYTICS_TRAFFIC_SOURCES)
export const analyticsDeviceClassSchema = z.enum(ANALYTICS_DEVICE_CLASSES)

const analyticsEventBaseSchema = z.object({
  eventId: z.guid(),
  source: analyticsTrafficSourceSchema,
  deviceClass: analyticsDeviceClassSchema,
  referrerHost: z.string().max(ANALYTICS_REFERRER_HOST_MAX_LENGTH).optional(),
})

/** Browser → BFF analytics event body (no visitor/session ids). */
export const ingestAnalyticsEventBrowserSchema = z.discriminatedUnion('eventType', [
  analyticsEventBaseSchema
    .extend({
      eventType: z.literal('ASSET_VIEW'),
      assetId: z.guid(),
    })
    .strict(),
  analyticsEventBaseSchema
    .extend({
      eventType: z.literal('BUNDLE_VIEW'),
      bundleId: z.guid(),
    })
    .strict(),
  analyticsEventBaseSchema
    .extend({
      eventType: z.literal('COLLECTION_VIEW'),
      collectionId: z.guid(),
    })
    .strict(),
  analyticsEventBaseSchema
    .extend({
      eventType: z.literal('COLLECTION_ITEM_CLICK'),
      collectionId: z.guid(),
      assetId: z.guid(),
    })
    .strict(),
  analyticsEventBaseSchema
    .extend({
      eventType: z.literal('DOWNLOAD_REQUESTED'),
      assetId: z.guid(),
      assetVersionId: z.guid(),
    })
    .strict(),
])

export type IngestAnalyticsEventBrowserBody = z.infer<typeof ingestAnalyticsEventBrowserSchema>

const recommendationEventBaseSchema = z.object({
  eventId: z.guid(),
  sourceAssetId: z.guid(),
  targetAssetId: z.guid(),
  slotPosition: z.number().int().min(0).max(11),
  exposureId: z.guid(),
  rankingVersion: z.string().min(1).max(64),
  expiresAt: z.string().min(1),
  exposureToken: z.string().length(64),
  candidateIds: z.array(z.guid()).min(1).max(12),
  deviceClass: analyticsDeviceClassSchema,
})

export const ingestRecommendationEventBrowserSchema = z.discriminatedUnion('eventType', [
  recommendationEventBaseSchema
    .extend({
      eventType: z.literal('IMPRESSION'),
    })
    .strict(),
  recommendationEventBaseSchema
    .extend({
      eventType: z.literal('CLICK'),
    })
    .strict(),
])

export type IngestRecommendationEventBrowserBody = z.infer<
  typeof ingestRecommendationEventBrowserSchema
>

export const checkoutAttributionBrowserSchema = z
  .object({
    source: analyticsTrafficSourceSchema.optional(),
    collectionId: z.guid().optional(),
    referrerHost: z.string().max(ANALYTICS_REFERRER_HOST_MAX_LENGTH).optional(),
  })
  .strict()

export type CheckoutAttributionBrowser = z.infer<typeof checkoutAttributionBrowserSchema>
