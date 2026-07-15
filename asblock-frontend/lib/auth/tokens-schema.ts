import { z } from 'zod'

/** Successful auth response body from AssetBlock Web API (camelCase JSON). */
export const tokensResponseSchema = z.object({
  accessToken: z.string().min(1),
  refreshToken: z.string().min(1),
  accessExpiresAt: z.string().min(1),
  refreshExpiresAt: z.string().min(1),
})

export type TokensPayload = z.infer<typeof tokensResponseSchema>
