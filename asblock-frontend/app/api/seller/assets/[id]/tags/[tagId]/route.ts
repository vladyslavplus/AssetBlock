import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { assertSameOrigin, forwardBackendResponse } from '@/lib/server/bff-http'

export async function DELETE(
  request: Request,
  context: { params: Promise<{ id: string; tagId: string }> },
) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const { id, tagId } = await context.params
  const store = await cookies()
  const res = await fetchBackendAuthorized(
    store,
    `/api/assets/${encodeURIComponent(id)}/tags/${encodeURIComponent(tagId)}`,
    { method: 'DELETE' },
  )
  return forwardBackendResponse(res)
}
