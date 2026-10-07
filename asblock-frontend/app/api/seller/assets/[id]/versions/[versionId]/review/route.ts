import { parseUuidParam } from '@/lib/server/bff-params'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'

export async function GET(
  request: Request,
  context: { params: Promise<{ id: string; versionId: string }> },
) {
  const { versionId } = await context.params
  const parsedVersionId = parseUuidParam('versionId', versionId)
  if (!parsedVersionId.ok) {
    return parsedVersionId.response
  }

  return proxyAuthenticatedBff(request, {
    path: `/api/users/me/asset-versions/${encodeURIComponent(parsedVersionId.value)}/review`,
    init: { method: 'GET' },
  })
}
