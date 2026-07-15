import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { assertSameOrigin, forwardBackendResponse } from '@/lib/server/bff-http'

export async function PATCH(request: Request, context: { params: Promise<{ id: string }> }) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const { id } = await context.params
  const store = await cookies()
  const path = `/api/users/me/notifications/${encodeURIComponent(id)}/unread`
  const res = await fetchBackendAuthorized(store, path, {
    method: 'PATCH',
  })
  return forwardBackendResponse(res)
}
