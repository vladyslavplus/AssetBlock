import {
  moderationBackendEndpoints,
  moderationBackendPath,
} from '@/lib/moderation/moderation-routes'
import { coverageAssessmentRequestSchema } from '@/lib/moderation/moderation-schemas'
import { parseUuidParam } from '@/lib/server/bff-params'
import {
  assertSameOrigin,
  BACKEND_JSON_BODY_LIMIT_BYTES,
  readBoundedJsonBody,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'

export async function POST(request: Request, context: { params: Promise<{ id: string }> }) {
  const originError = assertSameOrigin(request)
  if (originError) return originError
  const body = await readBoundedJsonBody(request, BACKEND_JSON_BODY_LIMIT_BYTES.coverageAssessment)
  if (!body.ok) return body.response
  const parsedId = parseUuidParam('id', (await context.params).id)
  if (!parsedId.ok) return parsedId.response
  const parsed = coverageAssessmentRequestSchema.safeParse(body.value)
  if (!parsed.success) return zodValidationProblemResponse(parsed.error)
  return proxyAuthenticatedBff(request, {
    path: moderationBackendPath('coverageAssessment', parsedId.value),
    init: {
      method: moderationBackendEndpoints.coverageAssessment.method,
      body: JSON.stringify(parsed.data),
      headers: { 'Content-Type': 'application/json' },
    },
  })
}
