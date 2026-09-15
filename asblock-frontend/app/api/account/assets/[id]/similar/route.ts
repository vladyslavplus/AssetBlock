import { z } from 'zod'
import { zodValidationProblemResponse } from '@/lib/server/bff-http'
import { parseUuidParam } from '@/lib/server/bff-params'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'
import {
  SIMILAR_ASSETS_DEFAULT_LIMIT,
  SIMILAR_ASSETS_DEFAULT_MODE,
  SIMILAR_ASSETS_MODES,
} from '@/lib/catalog/asset-detail-query'

const personalSimilarQuerySchema = z.object({
  limit: z.coerce.number().int().min(1).max(12).default(SIMILAR_ASSETS_DEFAULT_LIMIT),
  mode: z.enum(SIMILAR_ASSETS_MODES).default(SIMILAR_ASSETS_DEFAULT_MODE),
})

export async function GET(request: Request, { params }: { params: Promise<{ id: string }> }) {
  const { id } = await params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) {
    return parsedId.response
  }

  const url = new URL(request.url)
  const queryResult = personalSimilarQuerySchema.safeParse(
    Object.fromEntries(url.searchParams.entries()),
  )
  if (!queryResult.success) {
    return zodValidationProblemResponse(queryResult.error)
  }

  const qs = new URLSearchParams({
    limit: String(queryResult.data.limit),
    mode: queryResult.data.mode,
  })

  return proxyAuthenticatedBff(request, {
    path: `/api/users/me/assets/${encodeURIComponent(parsedId.value)}/similar?${qs.toString()}`,
    init: { method: 'GET' },
  })
}
