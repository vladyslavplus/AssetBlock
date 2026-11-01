import { zodValidationProblemResponse } from '@/lib/server/bff-http'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'
import {
  moderationBackendEndpoints,
  moderationBackendPath,
} from '@/lib/moderation/moderation-routes'
import { moderationQueueQuerySchema } from '@/lib/moderation/moderation-schemas'

export async function GET(request: Request) {
  const parsed = moderationQueueQuerySchema.safeParse(
    Object.fromEntries(new URL(request.url).searchParams.entries()),
  )
  if (!parsed.success) return zodValidationProblemResponse(parsed.error)
  const query = new URLSearchParams({
    claimFilter: parsed.data.claimFilter,
    pageSize: String(parsed.data.pageSize),
  })
  if (parsed.data.state) query.set('state', parsed.data.state)
  if (parsed.data.cursor) query.set('cursor', parsed.data.cursor)
  return proxyAuthenticatedBff(request, {
    path: `${moderationBackendPath('queue')}?${query.toString()}`,
    init: { method: moderationBackendEndpoints.queue.method },
  })
}
