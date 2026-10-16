import { parseUuidParam } from '@/lib/server/bff-params'
import { problemResponse } from '@/lib/server/bff-http'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'
import { sellerAnalysisRetryRequestSchema } from '@/lib/seller/seller-draft-schemas'

export async function POST(
  request: Request,
  context: { params: Promise<{ id: string; versionId: string }> },
) {
  const { id, versionId } = await context.params
  const asset = parseUuidParam('id', id)
  if (!asset.ok) return asset.response
  const version = parseUuidParam('versionId', versionId)
  if (!version.ok) return version.response

  let input: unknown
  try {
    input = await request.json()
  } catch {
    return problemResponse(400, 'ERR_VALIDATION_FAILED', 'Request body must be valid JSON.')
  }
  const parsed = sellerAnalysisRetryRequestSchema.safeParse(input)
  if (!parsed.success)
    return problemResponse(400, 'ERR_VALIDATION_FAILED', 'Analysis retry request is invalid.')
  return proxyAuthenticatedBff(request, {
    path: `/api/users/me/assets/${encodeURIComponent(asset.value)}/versions/${encodeURIComponent(version.value)}/analysis-retries`,
    init: { method: 'POST', body: JSON.stringify(parsed.data) },
    enforceSameOrigin: true,
  })
}
