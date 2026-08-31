import { cookies } from 'next/headers'
import { updateCollectionSchema } from '@/lib/collections/collection-schemas'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import {
  assertSameOrigin,
  forwardAuthenticatedBackendResponse,
  invalidJsonResponse,
  zodValidationProblemResponse,
} from '@/lib/server/bff-http'
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
    `/api/seller/collections/${encodeURIComponent(parsedId.value)}`,
    { method: 'GET', signal: request.signal },
  )
  return forwardAuthenticatedBackendResponse(res)
}

export async function PATCH(request: Request, context: { params: Promise<{ id: string }> }) {
  const originError = assertSameOrigin(request)
  if (originError) return originError

  const { id } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) {
    return parsedId.response
  }

  let json: unknown
  try {
    json = await request.json()
  } catch {
    return invalidJsonResponse()
  }
  const parsed = updateCollectionSchema.safeParse(json)
  if (!parsed.success) {
    return zodValidationProblemResponse(parsed.error)
  }

  const store = await cookies()
  const res = await fetchBackendAuthorized(
    store,
    `/api/seller/collections/${encodeURIComponent(parsedId.value)}`,
    {
      method: 'PATCH',
      body: JSON.stringify({
        title: parsed.data.title,
        description: parsed.data.description?.trim() ? parsed.data.description.trim() : null,
      }),
      headers: { 'Content-Type': 'application/json' },
      signal: request.signal,
    },
  )
  return forwardAuthenticatedBackendResponse(res)
}
