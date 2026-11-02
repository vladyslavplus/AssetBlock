import {
  assertSameOrigin,
  BACKEND_JSON_BODY_LIMIT_BYTES,
  readBoundedJsonBody,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'
import { parseUuidParam } from '@/lib/server/bff-params'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'
import { assetVersionSubmissionRequestSchema } from '@/lib/seller/seller-draft-schemas'

export async function POST(
  request: Request,
  context: { params: Promise<{ id: string; versionId: string }> },
) {
  const originError = assertSameOrigin(request)
  if (originError) return originError
  const body = await readBoundedJsonBody(request, BACKEND_JSON_BODY_LIMIT_BYTES.sellerSubmission)
  if (!body.ok) return body.response

  const { id, versionId } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) {
    return parsedId.response
  }
  const parsedVersionId = parseUuidParam('versionId', versionId)
  if (!parsedVersionId.ok) {
    return parsedVersionId.response
  }

  const parsed = assetVersionSubmissionRequestSchema.safeParse(body.value)
  if (!parsed.success) {
    return zodValidationProblemResponse(parsed.error)
  }

  return proxyAuthenticatedBff(request, {
    path: `/api/assets/${encodeURIComponent(parsedId.value)}/versions/${encodeURIComponent(parsedVersionId.value)}/submissions`,
    init: {
      method: 'POST',
      body: JSON.stringify(parsed.data),
      headers: { 'Content-Type': 'application/json' },
    },
  })
}
