import {
  assertSameOrigin,
  invalidJsonResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'
import { parseUuidParam } from '@/lib/server/bff-params'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'
import { leaveReviewFormSchema } from '@/lib/reviews/review-schemas'

export async function POST(request: Request, context: { params: Promise<{ assetId: string }> }) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const { assetId } = await context.params
  const parsedAssetId = parseUuidParam('assetId', assetId)
  if (!parsedAssetId.ok) {
    return parsedAssetId.response
  }

  const bodyText = await request.text()
  let bodyJson: unknown
  try {
    bodyJson = JSON.parse(bodyText)
  } catch {
    return invalidJsonResponse()
  }

  const parsed = leaveReviewFormSchema.safeParse(bodyJson)
  if (!parsed.success) {
    return zodValidationProblemResponse(parsed.error)
  }

  const path = `/api/reviews/assets/${encodeURIComponent(parsedAssetId.value)}/reviews`
  return proxyAuthenticatedBff(request, {
    path,
    init: {
      method: 'POST',
      body: JSON.stringify(parsed.data),
      headers: { 'Content-Type': 'application/json' },
    },
  })
}
