import { z } from 'zod'
import {
  ASSET_CATEGORY_NAME_MAX_LENGTH,
  ASSET_DESCRIPTION_MAX_LENGTH,
  ASSET_MAX_TAGS,
  ASSET_TAG_NAME_MAX_LENGTH,
  ASSET_TITLE_MAX_LENGTH,
} from '@/lib/contracts/marketplace-bounds'

export const listingCopilotEnqueueResponseSchema = z
  .object({
    jobId: z.string().uuid(),
    assetVersionId: z.string().uuid(),
  })
  .strict()

export type ListingCopilotEnqueueResponse = z.infer<typeof listingCopilotEnqueueResponseSchema>

export const listingCopilotSuggestionSchema = z
  .object({
    jobId: z.string().uuid(),
    assetVersionId: z.string().uuid(),
    title: z.string().min(1).max(ASSET_TITLE_MAX_LENGTH),
    description: z.string().max(ASSET_DESCRIPTION_MAX_LENGTH),
    category: z.string().min(1).max(ASSET_CATEGORY_NAME_MAX_LENGTH),
    tags: z.array(z.string().min(1).max(ASSET_TAG_NAME_MAX_LENGTH)).max(ASSET_MAX_TAGS),
    provider: z.enum(['OPENROUTER', 'OLLAMA']),
    actualModel: z.string().min(1).max(200),
    modelRevision: z.string().min(1).max(200).nullable(),
    upstreamProvider: z.string().min(1).max(128).nullable(),
    createdAt: z.string().datetime({ offset: true }),
  })
  .strict()

export type ListingCopilotSuggestion = z.infer<typeof listingCopilotSuggestionSchema>
