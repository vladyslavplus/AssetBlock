import { cookies } from 'next/headers'
import { reviseBundleSchema } from '@/lib/bundles/bundle-schemas'
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
    `/api/seller/bundles/${encodeURIComponent(parsedId.value)}`,
    {
      method: 'GET',
      signal: request.signal,
    },
  )
  return forwardAuthenticatedBackendResponse(res)
}

export async function PUT(request: Request, context: { params: Promise<{ id: string }> }) {
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
  const parsed = reviseBundleSchema.safeParse(json)
  if (!parsed.success) {
    return zodValidationProblemResponse(parsed.error)
  }

  const store = await cookies()
  const res = await fetchBackendAuthorized(
    store,
    `/api/seller/bundles/${encodeURIComponent(parsedId.value)}`,
    {
      method: 'PUT',
      body: JSON.stringify({
        title: parsed.data.title,
        description: parsed.data.description?.trim() ? parsed.data.description.trim() : null,
        price: parsed.data.price,
        assetIds: parsed.data.assetIds,
      }),
      headers: { 'Content-Type': 'application/json' },
      signal: request.signal,
    },
  )
  return forwardAuthenticatedBackendResponse(res)
}
