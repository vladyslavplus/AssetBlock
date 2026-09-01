import { parseUuidParam } from '@/lib/server/bff-params'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'

export async function DELETE(
  request: Request,
  context: { params: Promise<{ id: string; assetId: string }> },
) {
  const { id, assetId } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) {
    return parsedId.response
  }
  const parsedAssetId = parseUuidParam('assetId', assetId)
  if (!parsedAssetId.ok) {
    return parsedAssetId.response
  }

  return proxyAuthenticatedBff(request, {
    path: `/api/seller/collections/${encodeURIComponent(parsedId.value)}/items/${encodeURIComponent(parsedAssetId.value)}`,
    init: { method: 'DELETE' },
    enforceSameOrigin: true,
  })
}
