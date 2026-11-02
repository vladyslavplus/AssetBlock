import {
  assertSameOrigin,
  BACKEND_JSON_BODY_LIMIT_BYTES,
  readBoundedJsonBody,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'
import { parseUuidParam } from '@/lib/server/bff-params'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'
import { submissionWithdrawRequestSchema } from '@/lib/seller/seller-draft-schemas'

export async function POST(
  request: Request,
  context: { params: Promise<{ submissionId: string }> },
) {
  const originError = assertSameOrigin(request)
  if (originError) return originError
  const body = await readBoundedJsonBody(request, BACKEND_JSON_BODY_LIMIT_BYTES.sellerWithdraw)
  if (!body.ok) return body.response

  const { submissionId } = await context.params
  const parsedId = parseUuidParam('submissionId', submissionId)
  if (!parsedId.ok) {
    return parsedId.response
  }

  const parsed = submissionWithdrawRequestSchema.safeParse(body.value)
  if (!parsed.success) {
    return zodValidationProblemResponse(parsed.error)
  }

  return proxyAuthenticatedBff(request, {
    path: `/api/submissions/${encodeURIComponent(parsedId.value)}/withdraw`,
    init: {
      method: 'POST',
      body: JSON.stringify(parsed.data),
      headers: { 'Content-Type': 'application/json' },
    },
  })
}
