import { z } from 'zod'

import { checkoutAttributionBrowserSchema } from '@/lib/analytics/telemetry-schemas'

export const createCheckoutRequestSchema = z
  .object({
    assetId: z.guid('Asset ID must be a valid UUID.'),
    attribution: checkoutAttributionBrowserSchema.optional(),
    doNotTrack: z.literal(true).optional(),
  })
  .strict()

export type CreateCheckoutRequest = z.infer<typeof createCheckoutRequestSchema>

export const createBundleCheckoutRequestSchema = z
  .object({
    bundleId: z.guid('Bundle ID must be a valid UUID.'),
    attribution: checkoutAttributionBrowserSchema.optional(),
    doNotTrack: z.literal(true).optional(),
  })
  .strict()

export type CreateBundleCheckoutRequest = z.infer<typeof createBundleCheckoutRequestSchema>

export const createCheckoutResponseSchema = z.object({
  checkoutUrl: z.string().url(),
  checkoutIntentId: z.guid(),
})

export const CHECKOUT_FULFILLMENT_STATUSES = ['pending', 'completed', 'cancelled'] as const
export type CheckoutFulfillmentStatus = (typeof CHECKOUT_FULFILLMENT_STATUSES)[number]

export const checkoutStatusResponseSchema = z.object({
  status: z.enum(CHECKOUT_FULFILLMENT_STATUSES),
  checkoutIntentId: z.guid(),
  orderId: z.guid().nullable(),
  productTitle: z.string(),
  assetId: z.guid().nullable(),
  bundleId: z.guid().nullable(),
})
