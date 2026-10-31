import {
  sellerReviewBackendEndpoints,
  sellerReviewBackendPath,
} from '@/lib/seller/seller-review-routes'
import { parseUuidParam } from '@/lib/server/bff-params'
import {
  assertSameOrigin,
  invalidJsonResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'
import { coverageReviewCreateRequestSchema } from '@/lib/seller/seller-draft-schemas'

export async function POST(
  request: Request,
  context: { params: Promise<{ id: string; versionId: string }> },
) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const { id, versionId } = await context.params
  const asset = parseUuidParam('id', id)
  if (!asset.ok) return asset.response
  const version = parseUuidParam('versionId', versionId)
  if (!version.ok) return version.response

  let input: unknown
  try {
    input = await request.json()
  } catch {
    return invalidJsonResponse()
  }
  const parsed = coverageReviewCreateRequestSchema.safeParse(input)
  if (!parsed.success) return zodValidationProblemResponse(parsed.error)
  return proxyAuthenticatedBff(request, {
    path: sellerReviewBackendPath('coverage', asset.value, version.value),
    init: {
      method: sellerReviewBackendEndpoints.coverage.writeMethod,
      body: JSON.stringify(parsed.data),
      headers: { 'Content-Type': 'application/json' },
    },
    enforceSameOrigin: true,
  })
}
