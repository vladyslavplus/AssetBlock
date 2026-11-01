import {
  moderationBackendEndpoints,
  moderationBackendPath,
} from '@/lib/moderation/moderation-routes'
import { coverageQueueQuerySchema } from '@/lib/moderation/moderation-schemas'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'
import { zodValidationProblemResponse } from '@/lib/server/bff-http'

export async function GET(request: Request) {
  const parsed = coverageQueueQuerySchema.safeParse(
    Object.fromEntries(new URL(request.url).searchParams.entries()),
  )
  if (!parsed.success) return zodValidationProblemResponse(parsed.error)
  const query = new URLSearchParams({ page: String(parsed.data.page) })
  return proxyAuthenticatedBff(request, {
    path: `${moderationBackendPath('coverageQueue')}?${query}`,
    init: { method: moderationBackendEndpoints.coverageQueue.method, cache: 'no-store' },
  })
}
