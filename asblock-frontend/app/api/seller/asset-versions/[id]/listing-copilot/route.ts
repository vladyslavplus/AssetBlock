import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { assertSameOrigin, forwardAuthenticatedBackendResponse } from '@/lib/server/bff-http'
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
    `/api/users/me/asset-versions/${encodeURIComponent(parsedId.value)}/listing-copilot`,
    { method: 'GET', signal: request.signal },
  )
  return forwardAuthenticatedBackendResponse(res)
}

export async function POST(request: Request, context: { params: Promise<{ id: string }> }) {
  const originError = assertSameOrigin(request)
  if (originError) {
    return originError
  }

  const { id } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) {
    return parsedId.response
  }

  const store = await cookies()
  const res = await fetchBackendAuthorized(
    store,
    `/api/users/me/asset-versions/${encodeURIComponent(parsedId.value)}/listing-copilot`,
    { method: 'POST', signal: request.signal },
  )
  return forwardAuthenticatedBackendResponse(res)
}
