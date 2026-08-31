import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { assertSameOrigin, forwardAuthenticatedBackendResponse } from '@/lib/server/bff-http'
import { parseUuidParam } from '@/lib/server/bff-params'

export async function DELETE(
  request: Request,
  context: { params: Promise<{ id: string; tagId: string }> },
) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const { id, tagId } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) {
    return parsedId.response
  }
  const parsedTagId = parseUuidParam('tagId', tagId)
  if (!parsedTagId.ok) {
    return parsedTagId.response
  }

  const store = await cookies()
  const res = await fetchBackendAuthorized(
    store,
    `/api/assets/${encodeURIComponent(parsedId.value)}/tags/${encodeURIComponent(parsedTagId.value)}`,
    {
      method: 'DELETE',
      signal: request.signal,
    },
  )
  return forwardAuthenticatedBackendResponse(res)
}
