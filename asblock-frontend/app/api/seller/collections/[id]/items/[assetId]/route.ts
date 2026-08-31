import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { assertSameOrigin, forwardAuthenticatedBackendResponse } from '@/lib/server/bff-http'
import { parseUuidParam } from '@/lib/server/bff-params'

export async function DELETE(
  request: Request,
  context: { params: Promise<{ id: string; assetId: string }> },
) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const { id, assetId } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) {
    return parsedId.response
  }
  const parsedAssetId = parseUuidParam('assetId', assetId)
  if (!parsedAssetId.ok) {
    return parsedAssetId.response
  }

  const store = await cookies()
  const res = await fetchBackendAuthorized(
    store,
    `/api/seller/collections/${encodeURIComponent(parsedId.value)}/items/${encodeURIComponent(parsedAssetId.value)}`,
    {
      method: 'DELETE',
      signal: request.signal,
    },
  )
  return forwardAuthenticatedBackendResponse(res)
}
