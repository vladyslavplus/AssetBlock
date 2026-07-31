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
  eventId: z.string().uuid(),
  source: analyticsTrafficSourceSchema,
  deviceClass: analyticsDeviceClassSchema,
  referrerHost: z.string().max(ANALYTICS_REFERRER_HOST_MAX_LENGTH).optional(),
})

/** Browser → BFF analytics event body (no visitor/session ids). */
export const ingestAnalyticsEventBrowserSchema = z.discriminatedUnion('eventType', [
  analyticsEventBaseSchema
    .extend({
      eventType: z.literal('ASSET_VIEW'),
      assetId: z.string().uuid(),
    })
    .strict(),
  analyticsEventBaseSchema
    .extend({
      eventType: z.literal('BUNDLE_VIEW'),
      bundleId: z.string().uuid(),
    })
    .strict(),
  analyticsEventBaseSchema
    .extend({
      eventType: z.literal('COLLECTION_VIEW'),
      collectionId: z.string().uuid(),
    })
    .strict(),
  analyticsEventBaseSchema
    .extend({
      eventType: z.literal('COLLECTION_ITEM_CLICK'),
      collectionId: z.string().uuid(),
      assetId: z.string().uuid(),
    })
    .strict(),
  analyticsEventBaseSchema
    .extend({
      eventType: z.literal('DOWNLOAD_REQUESTED'),
      assetId: z.string().uuid(),
      assetVersionId: z.string().uuid(),
    })
    .strict(),
])

export type IngestAnalyticsEventBrowserBody = z.infer<typeof ingestAnalyticsEventBrowserSchema>

export const checkoutAttributionBrowserSchema = z
  .object({
    source: analyticsTrafficSourceSchema.optional(),
    collectionId: z.string().uuid().optional(),
    referrerHost: z.string().max(ANALYTICS_REFERRER_HOST_MAX_LENGTH).optional(),
  })
  .strict()

export type CheckoutAttributionBrowser = z.infer<typeof checkoutAttributionBrowserSchema>
