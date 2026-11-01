import {
  moderationBackendEndpoints,
  moderationBackendPath,
} from '@/lib/moderation/moderation-routes'
import { parseUuidParam } from '@/lib/server/bff-params'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'

export async function GET(request: Request, context: { params: Promise<{ id: string }> }) {
  const parsedId = parseUuidParam('id', (await context.params).id)
  if (!parsedId.ok) return parsedId.response
  return proxyAuthenticatedBff(request, {
    path: moderationBackendPath('case', parsedId.value),
    init: { method: moderationBackendEndpoints.case.method },
  })
}
