import {
  moderationBackendEndpoints,
  moderationBackendPath,
} from '@/lib/moderation/moderation-routes'
import { claimChangeRequestSchema } from '@/lib/moderation/moderation-schemas'
import { parseUuidParam } from '@/lib/server/bff-params'
import {
  assertSameOrigin,
  invalidJsonResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'

export async function POST(request: Request, context: { params: Promise<{ id: string }> }) {
  const originError = assertSameOrigin(request)
  if (originError) return originError
  const parsedId = parseUuidParam('id', (await context.params).id)
  if (!parsedId.ok) return parsedId.response
  let body: unknown
  try {
    body = await request.json()
  } catch {
    return invalidJsonResponse()
  }
  const parsed = claimChangeRequestSchema.safeParse(body)
  if (!parsed.success) return zodValidationProblemResponse(parsed.error)
  return proxyAuthenticatedBff(request, {
    path: moderationBackendPath('release', parsedId.value),
    init: {
      method: moderationBackendEndpoints.release.method,
      body: JSON.stringify(parsed.data),
      headers: { 'Content-Type': 'application/json' },
    },
    enforceSameOrigin: true,
  })
}
