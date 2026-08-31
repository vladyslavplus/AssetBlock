import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { forwardAuthenticatedBackendResponse } from '@/lib/server/bff-http'
import { parseUuidParam } from '@/lib/server/bff-params'

export async function GET(request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) {
    return parsedId.response
  }

  const store = await cookies()
  const res = await fetchBackendAuthorized(
    store,
    `/api/users/me/asset-versions/${encodeURIComponent(parsedId.value)}/processing-jobs`,
    { method: 'GET', signal: request.signal },
  )
  return forwardAuthenticatedBackendResponse(res)
}
