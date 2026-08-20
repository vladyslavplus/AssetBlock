import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { assertSameOrigin, forwardBackendResponse } from '@/lib/server/bff-http'

export async function DELETE(
  request: Request,
  context: { params: Promise<{ id: string; assetId: string }> },
) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const { id, assetId } = await context.params
  const store = await cookies()
  const res = await fetchBackendAuthorized(
    store,
    `/api/seller/collections/${encodeURIComponent(id)}/items/${encodeURIComponent(assetId)}`,
    { method: 'DELETE' },
  )
  return forwardBackendResponse(res)
}
