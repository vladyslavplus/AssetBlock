import { z } from 'zod'

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
    title: z.string().min(1).max(500),
    description: z.string().max(5000),
    category: z.string().min(1).max(200),
    tags: z.array(z.string().min(1).max(50)).max(10),
    provider: z.enum(['OPENROUTER', 'OLLAMA']),
    actualModel: z.string().min(1).max(200),
    modelRevision: z.string().min(1).max(200).nullable(),
    upstreamProvider: z.string().min(1).max(128).nullable(),
    createdAt: z.string().datetime({ offset: true }),
  })
  .strict()

export type ListingCopilotSuggestion = z.infer<typeof listingCopilotSuggestionSchema>
