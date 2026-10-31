import {
  sellerReviewBackendEndpoints,
  sellerReviewBackendPath,
} from '@/lib/seller/seller-review-routes'
import { parseUuidParam } from '@/lib/server/bff-params'
import {
  assertSameOrigin,
  invalidJsonResponse,
  zodValidationProblemResponse,
  problemResponse,
} from '@/lib/server/bff-http'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'
import { sellerResponseRequestSchema } from '@/lib/seller/seller-draft-schemas'

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
  const parsed = sellerResponseRequestSchema.safeParse(input)
  if (!parsed.success) return zodValidationProblemResponse(parsed.error)
  return proxyAuthenticatedBff(request, {
    path: sellerReviewBackendPath('responses', asset.value, version.value),
    init: {
      method: sellerReviewBackendEndpoints.responses.writeMethod,
      body: JSON.stringify(parsed.data),
      headers: { 'Content-Type': 'application/json' },
    },
    enforceSameOrigin: true,
  })
}

export async function GET(
  request: Request,
  context: { params: Promise<{ id: string; versionId: string }> },
) {
  const { id, versionId } = await context.params
  const asset = parseUuidParam('id', id)
  if (!asset.ok) return asset.response
  const version = parseUuidParam('versionId', versionId)
  if (!version.ok) return version.response
  const page = new URL(request.url).searchParams.get('page') ?? '1'
  if (!/^[1-9][0-9]{0,4}$/.test(page) || Number(page) > 10000)
    return problemResponse(400, 'ERR_VALIDATION_FAILED', 'Page must be between 1 and 10000.')
  return proxyAuthenticatedBff(request, {
    path: `${sellerReviewBackendPath('responses', asset.value, version.value)}?page=${page}`,
    init: { method: sellerReviewBackendEndpoints.responses.readMethod },
  })
}
