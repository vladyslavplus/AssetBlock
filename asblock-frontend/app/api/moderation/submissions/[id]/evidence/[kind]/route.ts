import {
  moderationBackendEndpoints,
  moderationBackendPath,
} from '@/lib/moderation/moderation-routes'
import { restrictedEvidenceKindSchema } from '@/lib/moderation/moderation-schemas'
import { parseUuidParam } from '@/lib/server/bff-params'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'
import { zodValidationProblemResponse } from '@/lib/server/bff-http'
import { z } from 'zod'

const querySchema = z.object({ findingId: z.string().uuid().optional() }).strict()

export async function GET(
  request: Request,
  context: { params: Promise<{ id: string; kind: string }> },
) {
  const params = await context.params
  const parsedId = parseUuidParam('id', params.id)
  if (!parsedId.ok) return parsedId.response
  const parsedKind = restrictedEvidenceKindSchema.safeParse(params.kind)
  if (!parsedKind.success) return zodValidationProblemResponse(parsedKind.error)
  const parsedQuery = querySchema.safeParse(
    Object.fromEntries(new URL(request.url).searchParams.entries()),
  )
  if (!parsedQuery.success) return zodValidationProblemResponse(parsedQuery.error)
  const query = parsedQuery.data.findingId
    ? `?findingId=${encodeURIComponent(parsedQuery.data.findingId)}`
    : ''
  return proxyAuthenticatedBff(request, {
    path: `${moderationBackendPath('caseEvidence', parsedId.value, parsedKind.data)}${query}`,
    init: { method: moderationBackendEndpoints.caseEvidence.method, cache: 'no-store' },
  })
}
