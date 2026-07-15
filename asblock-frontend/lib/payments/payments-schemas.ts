import { z } from 'zod'

export const createCheckoutRequestSchema = z
  .object({
    assetId: z.string().uuid('Asset ID must be a valid UUID.'),
  })
  .strict()

export type CreateCheckoutRequest = z.infer<typeof createCheckoutRequestSchema>
