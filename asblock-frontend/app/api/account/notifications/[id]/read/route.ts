import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { assertSameOrigin, forwardAuthenticatedBackendResponse } from '@/lib/server/bff-http'
import { parseUuidParam } from '@/lib/server/bff-params'

export async function PATCH(request: Request, context: { params: Promise<{ id: string }> }) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const { id } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) {
    return parsedId.response
  }

  const store = await cookies()
  const path = `/api/users/me/notifications/${encodeURIComponent(parsedId.value)}/read`
  const res = await fetchBackendAuthorized(store, path, {
    method: 'PATCH',
    signal: request.signal,
  })
  return forwardAuthenticatedBackendResponse(res)
}
